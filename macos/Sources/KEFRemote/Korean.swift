import Foundation

/// Korean text helpers for messages built from names and numbers. Same rules as the Windows app.
enum Korean {
    /// "로" or "으로" for the word in front, by how its last syllable is read: "Wi-Fi로", "30으로".
    static func ro(_ word: String) -> String {
        var w = word.trimmingCharacters(in: .whitespaces)
        // "TV (HDMI eARC)" is read as "TV".
        if let paren = w.range(of: " (") { w = String(w[..<paren.lowerBound]) }
        let scalars = Array(w.unicodeScalars)
        guard var last = scalars.last else { return "로" }
        if (0xAC00...0xD7A3).contains(last.value) {
            // No final consonant, or ㄹ, takes 로.
            let final = (last.value - 0xAC00) % 28
            return final == 0 || final == 8 ? "로" : "으로"
        }
        // 영, 삼, 육, 십, 백, 천, 만 end in a consonant other than ㄹ.
        if (0x30...0x39).contains(last.value) { return "036".unicodeScalars.contains(last) ? "으로" : "로" }
        // Latin names: "Stream", "Line" (silent e) end in a nasal; "Optical", "Wi-Fi", "USB-C" do not.
        if last == "e" || last == "E", scalars.count > 1 { last = scalars[scalars.count - 2] }
        return "mnMN".unicodeScalars.contains(last) ? "으로" : "로"
    }
}
