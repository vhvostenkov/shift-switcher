import AppKit
let out = CommandLine.arguments[1]
let fm = FileManager.default
try fm.createDirectory(atPath: out, withIntermediateDirectories: true)
for size in [16,32,64,128,256,512,1024] {
    let image = NSImage(size: NSSize(width: size, height: size))
    image.lockFocus()
    let s = CGFloat(size) / 1024
    let transform = NSAffineTransform()
    transform.scale(by: s)
    transform.concat()
    let rect = NSRect(x: 18, y: 18, width: 988, height: 988)
    let shape = NSBezierPath(roundedRect: rect, xRadius: 235, yRadius: 235)
    NSGradient(starting: NSColor(red: 0.84, green: 0.79, blue: 1, alpha: 1), ending: NSColor(red: 0.58, green: 0.49, blue: 0.79, alpha: 1))!.draw(in: shape, angle: -70)
    NSColor.white.withAlphaComponent(0.35).setStroke(); shape.lineWidth = 4; shape.stroke()
    let arrow = NSBezierPath()
    arrow.move(to: NSPoint(x: 512, y: 782)); arrow.line(to: NSPoint(x: 792, y: 477)); arrow.line(to: NSPoint(x: 620, y: 477)); arrow.line(to: NSPoint(x: 620, y: 235)); arrow.line(to: NSPoint(x: 404, y: 235)); arrow.line(to: NSPoint(x: 404, y: 477)); arrow.line(to: NSPoint(x: 232, y: 477)); arrow.close()
    NSColor(red: 0.16, green: 0.13, blue: 0.23, alpha: 1).setStroke()
    arrow.lineWidth = 48; arrow.lineJoinStyle = .round; arrow.stroke()
    image.unlockFocus()
    let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
    let png = rep.representation(using: .png, properties: [:])!
    for base in [16,32,128,256,512] {
        if size == base { try png.write(to: URL(fileURLWithPath: "\(out)/icon_\(base)x\(base).png")) }
        if size == base*2 { try png.write(to: URL(fileURLWithPath: "\(out)/icon_\(base)x\(base)@2x.png")) }
    }
}
