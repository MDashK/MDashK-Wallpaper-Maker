# MDashK Wallpaper Maker

Windows tool that turns anime illustrations into ready-to-use wallpapers in batch.
Every image is resized and cropped to the chosen screen size. Any space left over is filled with blurred mirror copies of the image, and the result is saved as PNG.

There are two separate apps:

| App | For | Layout |
|---|---|---|
| **MDashK Wallpaper Maker** | Desktop / laptop screens (1920x1080, 2560x1440, 4K, 21:9…) | Image fitted to the screen height, blurred mirrors on the left and right |
| **MDashK Wallpaper Maker Mobile** | Phones (1080x2400, iPhone, QHD+…) | Image fitted to the screen width, blurred mirrors above and below |

## Features

- **Batch processing** of a folder (optionally with sub-folders), selected files or drag & drop. Japanese and other special characters in file names are supported.
- **Smart framing (AI):** detects the anime character (head, half-body) and any text or watermarks near the borders, then chooses the zoom and crop automatically. Can be switched off globally or per image.
- **Preview / Adjust (optional):** see the final wallpaper before saving, drag to move, use the mouse wheel to zoom, center the image, or skip an image. Images you never open are processed automatically.
- **Learning:** your adjustments are recorded, and the automatic framing adapts to your taste over time.
- **waifu2x upscaling** (2x / 4x / 6x / 8x) inside the preview, for images that are too small or need more detail. Runs on the GPU (DirectML) or on the CPU.
- **Any output resolution:** presets or a custom size. Results go to `processed_output_<size>`. Images that could not be processed are moved to `not_processed`, with the reason shown in the log.
- **Portable:** settings and history are stored in a `config` folder next to the executable.

## Requirements

- Windows 10 / 11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional: a DirectX 12 graphics card for fast waifu2x upscaling

## Usage

1. Run `MDashK Wallpaper Maker.exe` (or the Mobile version).
2. Pick the **Wallpaper size**.
3. Click **Open folder** / **Open file**, or drag images onto the window.
4. Optional: click **Preview / Adjust** to check or change the framing of individual images.
5. Click **Process**.

## AI Disclosure

This project makes heavy use of Claude AI.

## Credits

- [waifu2x](https://github.com/nagadomi/nunif) by nagadomi: upscaling models (ONNX)
- [deepghs](https://huggingface.co/deepghs): anime head / face / half-body detection models
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR): text detection model
- [ONNX Runtime](https://github.com/microsoft/onnxruntime) with DirectML

Each model is distributed under its own license; see the linked projects.
