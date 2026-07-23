# MdPad

A lightweight, **native** Markdown editor and viewer for Windows, built with **WinUI 3** (.NET 10 / Windows App SDK).

The preview renders Markdown into native WinUI controls via [Markdig](https://github.com/xoofx/markdig) — **no WebView2, no embedded browser**.

## Features

- Live split preview (editor | rendered), plus editor-only and preview-only views
- Draggable splitter between panes
- Open / Save / Save As for `.md`, `.markdown`, `.txt` with unsaved-changes prompts
- Keyboard shortcuts: Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S
- Word / character count and dirty-state title marker
- Renders headings, lists (incl. task lists), tables, blockquotes, fenced code, links, images, emphasis, strikethrough

## Build & run

```powershell
dotnet build -c Debug
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MdPad.exe
```

Configured as an **unpackaged, self-contained** app, so the built exe runs standalone (no MSIX install, no separate runtime).

## Project layout

- `MainPage.xaml` / `.cs` — UI shell: toolbar, editor, preview host, status bar, file commands
- `MarkdownRenderer.cs` — Markdig AST → native WinUI control tree
- `MainWindow.xaml` / `.cs` — window, custom title bar
