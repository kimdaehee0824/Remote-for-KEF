import AppKit
import KefW2Driver

/// The speaker drawn in a KEF finish's colors, for the panel header and the finish picker.
/// Same shapes as the Windows app icon (Ui.AppGlyph): cabinet, trim ring, cone and tweeter on a 48-point grid.
enum SpeakerArt {
    @MainActor static func image(_ finish: KefFinish, size: CGFloat) -> NSImage {
        let key = "\(finish.id)@\(size)"
        if let cached = cache[key] { return cached }
        let image = MenuBarGlyph.render(size: CGSize(width: size, height: size), template: false) { context in
            context.scaleBy(x: size / 48, y: size / 48)
            fill(context, finish.cabinet, CGPath(roundedRect: CGRect(x: 10, y: 3, width: 28, height: 42), cornerWidth: 5, cornerHeight: 5, transform: nil))
            context.setStrokeColor(color(finish.edge))
            context.setLineWidth(1.5)
            context.addPath(CGPath(roundedRect: CGRect(x: 10.75, y: 3.75, width: 26.5, height: 40.5), cornerWidth: 4.25, cornerHeight: 4.25, transform: nil))
            context.strokePath()
            let driver: [(radius: CGFloat, rgb: UInt32)] = [(11, finish.trim), (8.2, finish.cone), (3.2, finish.tweeter)]
            for part in driver {
                let r = part.radius
                fill(context, part.rgb, CGPath(ellipseIn: CGRect(x: 24 - r, y: 24 - r, width: r * 2, height: r * 2), transform: nil))
            }
        }
        cache[key] = image
        return image
    }

    @MainActor private static var cache: [String: NSImage] = [:]

    private static func fill(_ context: CGContext, _ rgb: UInt32, _ path: CGPath) {
        context.setFillColor(color(rgb))
        context.addPath(path)
        context.fillPath()
    }

    private static func color(_ rgb: UInt32) -> CGColor {
        let channel = { (shift: UInt32) in CGFloat((rgb >> shift) & 0xFF) / 255 }
        return CGColor(srgbRed: channel(16), green: channel(8), blue: channel(0), alpha: 1)
    }
}
