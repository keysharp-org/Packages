# OCR

Recognizes text in an image and returns a structured result — lines and words, each with its text
and bounding box — plus helpers for highlighting, clicking, searching and sorting results.

```ahk
#Include <KPM/Keysharp/OCR>

result := OCR(image)
for line in result.Lines
    OutputDebug(line.Text)
```

## Pluggable engines

OCR does no recognition itself and no screen capture: the image comes from Keysharp's cross-platform
`Image` class, and recognition goes to whatever `OCR.Engine` is set to. Left unset it defaults to
Tesseract, which runs on Windows, Linux and macOS. Any object implementing `Recognize(image, options)`
can replace it — a cloud OCR, `Windows.Media.Ocr`, PaddleOCR:

```ahk
OCR.Engine := MyEngine()
```

That is the reason this is one package rather than one per backend: the engine is a runtime choice,
not a packaging one.

## Requirements

The default engine needs Tesseract on the system. On Windows, Tesseract cannot read non-ASCII paths;
on Linux and macOS UTF-8 paths work.

Because it uses Keysharp's `Image` and `Highlight` classes, this is a Keysharp package and does not
run on AutoHotkey.

## Versioning

Versioned by the registry (`0.x`): the numbers describe this Keysharp port, not the upstream
[Descolada/OCR](https://github.com/Descolada/OCR) releases it derives from.
