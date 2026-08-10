import SwiftUI

/// One compact progress treatment shared by project and session rows.  Keeping
/// it here prevents the two detail hierarchies from drifting apart again.
struct DetailProgressBar: View {
    let value: Double
    let tint: Color
    let track: Color

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(track)
                Capsule()
                    .fill(tint)
                    .frame(width: geometry.size.width * min(1, max(0, value)))
            }
        }
        .frame(height: 3)
        .accessibilityValue("\(Int((min(1, max(0, value)) * 100).rounded()))%")
    }
}
