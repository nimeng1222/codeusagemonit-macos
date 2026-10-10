import AppKit

// 应用图标位图：深底圆角 + 三根彩色柱子
func render(_ s: CGFloat) -> NSImage {
    let img = NSImage(size: NSSize(width: s, height: s))
    img.lockFocus()
    guard let ctx = NSGraphicsContext.current?.cgContext else { img.unlockFocus(); return img }
    ctx.clear(CGRect(x: 0, y: 0, width: s, height: s))
    let d = max(4, (s * 0.44).rounded())
    let rect = CGRect(x: 0.5, y: 0.5, width: s - 1, height: s - 1)
    let path = CGPath(roundedRect: rect, cornerWidth: d / 2, cornerHeight: d / 2, transform: nil)
    ctx.setFillColor(CGColor(red: 30/255, green: 33/255, blue: 39/255, alpha: 1))
    ctx.addPath(path); ctx.fillPath()
    ctx.setShouldAntialias(false)
    let w = max(2, (s * 0.15).rounded())
    let gap = max(1, (s * 0.07).rounded())
    var x = ((s - (3 * w + 2 * gap)) / 2).rounded(.down)
    let base = (s * 0.79).rounded()
    let bars: [(CGFloat, CGFloat, CGFloat, CGFloat)] = [
        (0.30, 109/255, 224/255, 214/255),
        (0.56, 150/255, 217/255, 238/255),
        (0.42, 214/255, 193/255, 249/255),
    ]
    for (hf, r, g, b) in bars {
        let h = max(2, (s * hf).rounded())
        ctx.setFillColor(CGColor(red: r, green: g, blue: b, alpha: 1))
        ctx.fill(CGRect(x: x, y: s - base, width: w, height: h))  // AppKit y 轴向上：base 从底部量
        x += w + gap
    }
    img.unlockFocus()
    return img
}

func savePng(_ img: NSImage, _ path: String) {
    guard let tiff = img.tiffRepresentation,
          let rep = NSBitmapImageRep(data: tiff),
          let png = rep.representation(using: .png, properties: [:]) else { return }
    try? png.write(to: URL(fileURLWithPath: path))
}

let dir = CommandLine.arguments[1]
for size in [16, 32, 128, 256, 512] {
    savePng(render(CGFloat(size)), "\(dir)/icon_\(size)x\(size).png")
    savePng(render(CGFloat(size * 2)), "\(dir)/icon_\(size)x\(size)@2x.png")
}
print("iconset sources written")
