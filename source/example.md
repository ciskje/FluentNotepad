# Sample guide

Welcome to **FluentNotepad** with *Markdown preview*! Visit [Microsoft](https://www.microsoft.com) or <https://dotnet.microsoft.com>.

## Formatting

Text with `inline code`, **bold**, *italic* and ~~strikethrough~~. Escape: \*not italic\*.

> An elegant quote
> on two lines.

---

### Lists

- Apples
- Pears
  - Nested sub-item
  - Another item
- [ ] todo
- [x] done

1. First step
2. Second step
10. Custom numbering

### Code

```csharp
var x = 42;
Console.WriteLine($"Value: {x}");
```

| Name  | Age | City   |
|:------|----:|:------:|
| Anna  |  28 | Rome   |
| Mark  |  35 | Milan  |

## Images

Path relative to the file's folder:

![Sample logo](example.png)

Same image inline: ![logo](example.png) with text around it.

With query string (e.g. GitHub's `?raw=true`) or fragments (`#center`):

![logo with query](example.png?raw=true)

Missing file (shows a graceful placeholder):

![missing](this-does-not-exist.png)

Setext title
============

Subtitle
--------
