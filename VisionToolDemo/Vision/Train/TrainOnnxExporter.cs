using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VisionToolDemo.Vision.Train
{
    /// <summary>
    /// 把 DeepTrainer 训练出的模型导出为标准 .onnx 文件（ONNX 算子集 13，IR 8）。
    ///
    /// 为什么手写 protobuf 而不引 onnx 生成库：
    ///   · 训练器网络结构固定（Conv→ReLU→MaxPool ×2 → Flatten → MatMul → Add），
    ///     序列化路径可以压缩到最小——不需要完整 onnx 库（那会引入一堆依赖）；
    ///   · 手写 wire format 输出可被 ONNX Runtime / Python onnxruntime 直接加载，
    ///     「深度学习推理」算子分类后端即用它（冒烟里用 python onnxruntime 实测验证）。
    ///
    /// 网络图（输入 "input" [1,3,S,S] FLOAT，输出 "output" [1,C] logits）：
    ///   Conv(k3 pad1 stride1) → Relu → MaxPool(k2 s2) → Conv(k3 pad1 s1) → Relu
    ///   → MaxPool(k2 s2) → Flatten(axis1) → MatMul(FC^T [D,C]) → Add(bias [C])
    /// 常量（initializer）：c1w/c1b/c2w/c2b/fcT/fcb，float32 little-endian raw_data。
    /// 分类后端在推理端做 softmax，所以这里只导 logits（与训练一致，等价）。
    /// </summary>
    public static class TrainOnnxExporter
    {
        /// <summary>导出 .onnx 到指定路径（同时返回字节；目录不存在自动创建）</summary>
        public static byte[] Export(DeepTrainer.TrainedModel model, string onnxPath)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            byte[] bytes = Build(model);
            string dir = Path.GetDirectoryName(onnxPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(onnxPath, bytes);
            return bytes;
        }

        /// <summary>构建 ONNX 模型字节</summary>
        public static byte[] Build(DeepTrainer.TrainedModel m)
        {
            int S = m.InputSide;
            int C = m.Labels.Count;
            int D = m.FcW.Length / C;   // 展平维

            var buf = new ByteWriter();

            // ============ ModelProto ============
            buf.FieldVarint(1, 8);                        // ir_version: 8
            buf.FieldMessage(8, (w) =>                    // opset_import { domain:"" version:13 }
            {
                w.FieldString(1, "");
                w.FieldVarint(2, 13);
            });
            buf.FieldString(2, "VisionToolDemo.Trainer");  // producer_name
            buf.FieldString(3, "0.1.0");                   // producer_version

            // ============ GraphProto ============
            buf.FieldMessage(7, (w) =>
            {
                w.FieldString(2, "vision_train_graph");   // GraphProto.name 必填（非空）
                // --- input 值信息 [1,3,S,S]（elem_type=FLOAT=1）---
                w.FieldMessage(11, (wi) => ValueInfo(wi, "input", 1, new long[] { 1, 3, S, S }));
                // --- output 值信息 [1,C]（elem_type=FLOAT=1）---
                w.FieldMessage(12, (wo) => ValueInfo(wo, "output", 1, new long[] { 1, C }));

                // --- 节点 ---
                Node(w, "Conv", new[] { "input", "c1w", "c1b" }, new[] { "c1" }, (n) =>
                {
                    AttrInts(n, "kernel_shape", 3, 3);
                    AttrInts(n, "strides", 1, 1);
                    AttrInts(n, "pads", 1, 1, 1, 1);
                });
                Node(w, "Relu", new[] { "c1" }, new[] { "r1" }, null);
                Node(w, "MaxPool", new[] { "r1" }, new[] { "p1" }, (n) =>
                {
                    AttrInts(n, "kernel_shape", 2, 2);
                    AttrInts(n, "strides", 2, 2);
                });
                Node(w, "Conv", new[] { "p1", "c2w", "c2b" }, new[] { "c2" }, (n) =>
                {
                    AttrInts(n, "kernel_shape", 3, 3);
                    AttrInts(n, "strides", 1, 1);
                    AttrInts(n, "pads", 1, 1, 1, 1);
                });
                Node(w, "Relu", new[] { "c2" }, new[] { "r2" }, null);
                Node(w, "MaxPool", new[] { "r2" }, new[] { "p2" }, (n) =>
                {
                    AttrInts(n, "kernel_shape", 2, 2);
                    AttrInts(n, "strides", 2, 2);
                });
                Node(w, "Flatten", new[] { "p2" }, new[] { "flat" }, null);   // axis=1 默认
                Node(w, "MatMul", new[] { "flat", "fcT" }, new[] { "mm" }, null);
                Node(w, "Add", new[] { "mm", "fcb" }, new[] { "output" }, null);

                // --- 常量 ---
                Tensor(w, "c1w", new long[] { 8, 3, 3, 3 }, m.Conv1W);
                Tensor(w, "c1b", new long[] { 8 }, m.Conv1B);
                Tensor(w, "c2w", new long[] { 16, 8, 3, 3 }, m.Conv2W);
                Tensor(w, "c2b", new long[] { 16 }, m.Conv2B);
                // FC 转置 [D,C]
                float[] fcT = new float[D * C];
                for (int c = 0; c < C; c++)
                    for (int d = 0; d < D; d++)
                        fcT[d * C + c] = m.FcW[c * D + d];
                Tensor(w, "fcT", new long[] { D, C }, fcT);
                Tensor(w, "fcb", new long[] { C }, m.FcB);
            });

            return buf.ToArray();
        }

        /// <summary>ValueInfoProto：name(1) + type(2, 非 3——3 是 doc_string) + TypeProto(tensor_type FLOAT 形状)</summary>
        private static void ValueInfo(ByteWriter w, string name, int elemType, long[] dims)
        {
            w.FieldString(1, name);
            w.FieldMessage(2, (t) =>          // type: TypeProto
            {
                t.FieldMessage(1, (tt) =>     // tensor_type: TensorTypeProto
                {
                    tt.FieldVarint(1, elemType);   // elem_type FLOAT=1
                    tt.FieldMessage(2, (sh) =>     // shape: TensorShapeProto
                    {
                        foreach (long d in dims)
                            sh.FieldMessage(1, (dim) => dim.FieldVarint(1, (ulong)d));  // dim_value
                    });
                });
            });
        }

        /// <summary>AttributeProto(name + type=INTS + packed ints) 便捷写入：每个 attribute 一条完整消息。
        /// 字段号：name=1、type=20、ints=8（ONNX AttributeProto 定义）</summary>
        private static void AttrInts(ByteWriter n, string name, params int[] values)
        {
            n.FieldMessage(5, (a) =>        // GraphProto.node[].attribute 字段=5
            {
                a.FieldString(1, name);     // AttributeProto.name=1
                a.FieldVarint(20, 7);       // AttributeProto.type=20，INTS=7
                foreach (int v in values) a.FieldVarint(8, v);   // AttributeProto.ints=8（packed varint）
            });
        }

        /// <summary>NodeProto：op_type + input/output 名 + 可选 attribute 回调</summary>
        private static void Node(ByteWriter w, string op, string[] inputs, string[] outputs,
            Action<ByteWriter> attrWriter)
        {
            w.FieldMessage(1, (ni) =>
            {
                foreach (string s in inputs) ni.FieldString(1, s);
                foreach (string s in outputs) ni.FieldString(2, s);
                ni.FieldString(4, op);                 // op_type
                attrWriter?.Invoke(ni);                // attribute（一次写完所有 attribute 字段）
            });
        }

        /// <summary>TensorProto：dims(1) + data_type(2) + name(8) + raw_data(9)，float32 LE。
        /// 字段号严格按 ONNX TensorProto 定义，此前 name 误写为字段 1 与 dims 冲突导致导出名空</summary>
        private static void Tensor(ByteWriter w, string name, long[] dims, float[] data)
        {
            w.FieldMessage(5, (t) =>
            {
                foreach (long d in dims) t.FieldVarint(1, (ulong)d);   // dims=1
                t.FieldVarint(2, 1);                                    // data_type=2 FLOAT=1
                t.FieldString(8, name);                                 // name=8
                byte[] raw = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
                t.FieldBytes(9, raw);                                   // raw_data=9
            });
        }

        /// <summary>手写 protobuf 编码器（只用到 varint / length-delimited 两种 wire type）</summary>
        private sealed class ByteWriter
        {
            private readonly List<byte> _b = [];

            public void FieldVarint(int field, ulong value)
            {
                WriteTag(field, 0);
                WriteVarint(value);
            }

            public void FieldVarint(int field, int value) => FieldVarint(field, (ulong)value);

            public void FieldString(int field, string s) => FieldBytes(field, Encoding.UTF8.GetBytes(s));

            public void FieldBytes(int field, byte[] data)
            {
                WriteTag(field, 2);
                WriteVarint((ulong)data.Length);
                _b.AddRange(data);
            }

            public void FieldMessage(int field, Action<ByteWriter> sub)
            {
                var inner = new ByteWriter();
                sub(inner);
                FieldBytes(field, inner.ToArray());
            }

            private void WriteTag(int field, int wireType)
            {
                // tag = (field << 3) | wireType，以 varint 写
                WriteVarint((ulong)((field << 3) | wireType));
            }

            private void WriteVarint(ulong v)
            {
                while (v >= 0x80)
                {
                    _b.Add((byte)(v | 0x80));
                    v >>= 7;
                }
                _b.Add((byte)v);
            }

            public byte[] ToArray() => _b.ToArray();
        }
    }
}
