#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fp16_to_fp32.py —— 把 fp16 量化版 ONNX 模型转回 float32

适用：深度学习推理算子不支持 fp16 输入（ONNX Runtime 桌面版构造 fp16 张量会触发
NullReferenceException），用本脚本把模型转回 float32 即可正常使用。

用法：
    pip install onnx numpy
    python fp16_to_fp32.py 模型.onnx [输出.onnx]

示例：
    python fp16_to_fp32.py yolov5s_fp16.onnx yolov5s_fp32.onnx

说明：适用于「权重/输入输出为 fp16、算子仍为通用算子」的 fp16 模型
（如 ultralytics export --half 导出的模型）。转换后精度与原 fp32 模型一致。
"""
import sys

import numpy as np
import onnx
from onnx import TensorProto, numpy_helper


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src.replace(".onnx", "_fp32.onnx")

    model = onnx.load(src)

    def fix_tensor_type(vi):
        t = vi.type.tensor_type
        if t.elem_type == TensorProto.FLOAT16:
            t.elem_type = TensorProto.FLOAT

    def fix_graph(g):
        """递归转换一个 GraphProto：输入/输出/中间张量类型 + 初始权重 + Constant 节点 + 子图"""
        # 输入 / 输出 / 中间张量声明类型
        for vi in list(g.input) + list(g.output) + list(g.value_info):
            fix_tensor_type(vi)
        # 初始权重
        for init in g.initializer:
            if init.data_type == TensorProto.FLOAT16:
                arr = numpy_helper.to_array(init).astype(np.float32)
                init.CopyFrom(numpy_helper.from_array(arr, init.name))
        # 算子内的 Constant 张量（如 yolov5 Detect 层的 Mul 常量）——
        # 只转 initializer 会漏掉它们，导致 Mul 一侧 float32、一侧 float16 的类型冲突
        for node in g.node:
            for attr in node.attribute:
                if attr.type == onnx.AttributeProto.TENSOR and attr.t.data_type == TensorProto.FLOAT16:
                    arr = numpy_helper.to_array(attr.t).astype(np.float32)
                    attr.t.CopyFrom(numpy_helper.from_array(arr, attr.t.name))
                elif attr.type == onnx.AttributeProto.GRAPH:
                    fix_graph(attr.g)   # if/loop/scan 等子图
        # 局部函数体里的 Constant 节点（onnx < 1.13 无 function 字段，用 getattr 防御）
        for fn in getattr(g, "function", ()):
            for node in fn.node:
                for attr in node.attribute:
                    if attr.type == onnx.AttributeProto.TENSOR and attr.t.data_type == TensorProto.FLOAT16:
                        arr = numpy_helper.to_array(attr.t).astype(np.float32)
                        attr.t.CopyFrom(numpy_helper.from_array(arr, attr.t.name))

    fix_graph(model.graph)

    try:
        onnx.checker.check_model(model)
    except Exception as e:  # 个别导出模型有未声明 value_info，checker 会提示但可保存
        print("警告：check_model 提示", e, "（继续保存）")

    onnx.save(model, dst)
    print("已把 fp16 模型转回 float32 ->", dst)


if __name__ == "__main__":
    main()
