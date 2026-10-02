**Language**: [English](README.md) | [简体中文](README.zh.md)

---

# VisionToolDemo — Vision Inspection Workbench (WPF)

A desktop vision inspection and automation workbench built with C# / .NET 8 + WPF. It supports vision pipeline orchestration, geometric measurement, OCR text recognition, deep-learning training and inference, ROI selection, real-time object tracking, and browser automation — suitable for industrial vision inspection, image-processing education, and automation flow demos.

## Features

- **Vision pipeline**: operator chain / node system with flow orchestration, conditional branches, loops, variables, and expressions
- **Operator library**:
  - Image preprocessing: grayscale, binarization/threshold, filters, morphology, resize/rotate, paint
  - Feature extraction: edge/contour, caliper, template matching
  - Geometric measurement: distance, angle (3-point angle / angle between two lines), circle, line, area, perimeter, fitting, eccentricity, concentricity, parallelism, perpendicularity, ellipse, etc.
  - Recognition: barcode/QR recognition (multiple symbologies), OCR text recognition (Tesseract, multi-line, multiple engines)
  - Deep learning: classification / object detection / similarity comparison inference
- **Deep-learning training**:
  - Training page: data annotation (labelimg-style visual editing, label pool), data augmentation, YOLOv5 training, confusion matrix, ONNX export
  - Inference: sliding-window multi-person / multi-object detection, face detection enhancement (YuNet preferred + Haar fallback), ROI-limited inference, label & confidence overlay on results
- **ROI selection**: synchronized selection on source/result images, supports arbitrary image sizes, selection position matches the actual mouse position
- **Automation**: workflow nodes (open file/browser, mouse move & click, popup prompts, conditional branches, loop control), with random offset to simulate human operation
- **Real-time tracking**: camera/video real-time object tracking, with face-tracking option
- **UI**: multiple theme switching (Dark Blue Gray / Dark Green / Dark Violet / Warm Orange Red / Light), all pages follow the theme

## Tech Stack

| Category | Choice |
|---|---|
| Language / Framework | C#, .NET 8 (net8.0-windows), WPF + MVVM |
| Image processing | OpenCvSharp (OpenCV 4) |
| OCR | Tesseract (Chinese/English language packs) |
| Deep learning | ONNX Runtime (YOLOv5 and other models), YuNet / Haar face detection |
| Testing | xUnit, including WIDER face enhancement, multi-object detection, node coverage regression tests |

## Directory Structure

```
VisionToolDemo/
├── Vision/              # Vision core: task/operator implementations, deep-learning training & inference, face detection
├── Wpf/                 # WPF UI: pages, theme styles, MVVM view models
├── VisionToolDemo.Tests/# Unit tests and test resources
├── docs/                # Illustrated usage guide
├── test_models/         # Test models
└── test_workflows/      # Test workflows
```

## Quick Start

1. Requirements: Windows 10/11, .NET 8 SDK
2. Open `VisionToolDemo.slnx` and restore NuGet packages
3. Run (F5)
4. Run unit tests:

```bash
dotnet test VisionToolDemo.Tests/VisionToolDemo.Tests.csproj
```

## Documentation

- [Deep-Learning Usage Guide](docs/深度学习使用说明.md)
- [Object Tracking Guide](docs/目标跟踪说明.md)
- [Training Guide](docs/训练功能说明.md)

## Notes

- The deep-learning models bundled (e.g., the YuNet face-detection ONNX) are open-source models; training and inference interfaces follow the ONNX standard, so you can replace them with your own models.
- The project uses open-source, free, commercially-usable components (OpenCV, Tesseract, ONNX Runtime, etc.).
