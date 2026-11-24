import SwiftUI

enum Theme {
    static let bg = Color(hex: "#0E1014")
    static let panel = Color(hex: "#171B22")
    static let edge = Color(hex: "#2C3240")
    static let text = Color(hex: "#E8EBF1")
    static let dim = Color(hex: "#8A92A6")
    static let accent = Color(hex: "#39FF88")
    static let warn = Color(hex: "#FFCA28")
    static let bad = Color(hex: "#EF5350")
}

extension Color {
    init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("#") { s.removeFirst() }
        var v: UInt64 = 0
        Scanner(string: s).scanHexInt64(&v)
        let r, g, b, a: Double
        if s.count == 8 {
            r = Double((v >> 24) & 0xFF) / 255; g = Double((v >> 16) & 0xFF) / 255
            b = Double((v >> 8) & 0xFF) / 255; a = Double(v & 0xFF) / 255
        } else {
            r = Double((v >> 16) & 0xFF) / 255; g = Double((v >> 8) & 0xFF) / 255
            b = Double(v & 0xFF) / 255; a = 1
        }
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    /// "#RRGGBB" for storing a ColorPicker choice in a layout.
    var hexString: String {
        let ui = UIColor(self)
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        ui.getRed(&r, green: &g, blue: &b, alpha: &a)
        func c(_ v: CGFloat) -> Int { Int((max(0, min(1, v)) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", c(r), c(g), c(b))
    }
}

/// Where a control sits on screen for a given pad size.
func controlFrame(_ c: Control, in size: CGSize) -> CGRect {
    let w = CGFloat(c.w) * size.height, h = CGFloat(c.h) * size.height
    return CGRect(x: CGFloat(c.x) * size.width - w / 2, y: CGFloat(c.y) * size.height - h / 2, width: w, height: h)
}

struct ControlShapeView: Shape {
    let shape: ControlShape
    func path(in rect: CGRect) -> Path {
        switch shape {
        case .circle: return Path(ellipseIn: rect)
        case .rounded: return Path(roundedRect: rect, cornerRadius: min(rect.width, rect.height) * 0.22)
        case .pill: return Path(roundedRect: rect, cornerRadius: min(rect.width, rect.height) / 2)
        }
    }
}
