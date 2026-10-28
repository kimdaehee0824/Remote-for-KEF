import AppKit

/// Menu bar glyph: a bookshelf speaker with one concentric Uni-Q driver in the middle.
/// Drawn as a template image so the system tints it for every menu bar appearance.
/// State is told by shape, not color: filled, filled with a slash, outline, outline with a slash.
enum MenuBarGlyph: CaseIterable {
    case on
    case muted
    case standby
    case offline

    @MainActor var image: NSImage { Self.images[self]! }

    @MainActor private static let images = Dictionary(uniqueKeysWithValues: allCases.map { ($0, $0.makeImage()) })

    private static let size = CGSize(width: 16, height: 16)
    private static let center = CGPoint(x: 8, y: 8)

    private func makeImage() -> NSImage {
        let glyph = self
        let image = NSImage(size: Self.size, flipped: false) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            glyph.draw(in: context)
            return true
        }
        image.isTemplate = true
        return image
    }

    private func draw(in context: CGContext) {
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        context.setStrokeColor(CGColor(gray: 0, alpha: 1))
        switch self {
        case .on:
            Self.fillSpeaker(in: context)
        case .muted:
            Self.withSlashGap(in: context) { Self.fillSpeaker(in: $0) }
        case .standby:
            Self.strokeSpeaker(in: context)
        case .offline:
            Self.withSlashGap(in: context) { Self.strokeSpeaker(in: $0) }
        }
    }

    /// Solid cabinet. Even-odd leaves a gap ring around the cone and opens the tweeter.
    private static func fillSpeaker(in context: CGContext) {
        let path = CGMutablePath()
        path.addPath(CGPath(roundedRect: CGRect(x: 2.5, y: 0.5, width: 11, height: 15), cornerWidth: 2.6, cornerHeight: 2.6, transform: nil))
        path.addEllipse(in: circle(radius: 4.5))
        path.addEllipse(in: circle(radius: 3))
        path.addEllipse(in: circle(radius: 1.25))
        context.addPath(path)
        context.fillPath(using: .evenOdd)
    }

    private static func strokeSpeaker(in context: CGContext) {
        context.setLineWidth(1.2)
        context.addPath(CGPath(roundedRect: CGRect(x: 3.1, y: 1.1, width: 9.8, height: 13.8), cornerWidth: 2.1, cornerHeight: 2.1, transform: nil))
        context.addEllipse(in: circle(radius: 3.3))
        context.strokePath()
        context.fillEllipse(in: circle(radius: 1.1))
    }

    /// Draws the speaker with a clear band cut out along the slash, then the slash itself,
    /// so the slash stays readable over the filled cabinet at 16 pt.
    private static func withSlashGap(in context: CGContext, drawSpeaker: (CGContext) -> Void) {
        let slash = CGMutablePath()
        slash.move(to: CGPoint(x: 1.6, y: 14.8))
        slash.addLine(to: CGPoint(x: 14.4, y: 0.8))

        let clip = CGMutablePath()
        clip.addRect(CGRect(origin: .zero, size: size).insetBy(dx: -4, dy: -4))
        clip.addPath(slash.copy(strokingWithWidth: 3.4, lineCap: .round, lineJoin: .round, miterLimit: 10))
        context.saveGState()
        context.addPath(clip)
        context.clip(using: .evenOdd)
        drawSpeaker(context)
        context.restoreGState()

        context.setLineWidth(1.3)
        context.setLineCap(.round)
        context.addPath(slash)
        context.strokePath()
    }

    private static func circle(radius: CGFloat) -> CGRect {
        CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
    }
}

extension MenuBarGlyph {
    /// The glyph followed by the volume in a slot two digits wide, drawn as one
    /// template image so the menu bar item keeps its width going from 9 to 10.
    @MainActor static func label(_ glyph: MenuBarGlyph, volume: Int?) -> NSImage {
        guard let volume else { return glyph.image }
        let key = LabelKey(glyph: glyph, volume: volume)
        if let cached = labels[key] { return cached }

        let font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.menuBarFont(ofSize: 0).pointSize, weight: .regular)
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black]
        let text = NSAttributedString(string: "\(volume)", attributes: attributes)
        let slot = max(NSAttributedString(string: "88", attributes: attributes).size().width, text.size().width)
        let gap: CGFloat = 4
        let glyphImage = glyph.image
        let image = NSImage(size: CGSize(width: ceil(size.width + gap + slot), height: size.height), flipped: false) { _ in
            glyphImage.draw(in: CGRect(origin: .zero, size: size))
            // Digits centered on the glyph by cap height; draw(at:) takes the bottom of the line, below the baseline.
            let baseline = ((size.height - font.capHeight) / 2).rounded()
            text.draw(at: CGPoint(x: size.width + gap, y: baseline + font.descender))
            return true
        }
        image.isTemplate = true
        labels[key] = image
        return image
    }

    private struct LabelKey: Hashable {
        let glyph: MenuBarGlyph
        let volume: Int
    }

    @MainActor private static var labels: [LabelKey: NSImage] = [:]
}
