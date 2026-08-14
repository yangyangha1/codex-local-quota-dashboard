import Foundation

/// Shared geometry rules for the floating desktop panel.  Keeping these values
/// in one place prevents restored frames and live resizing from drifting apart.
enum WidgetWindowGeometry {
    static let minimumSize = CGSize(width: 256, height: 278)
    static let maximumSize = CGSize(width: 576, height: 625)

    static func constrainedSize(_ proposed: CGSize) -> CGSize {
        let safeWidth = proposed.width.isFinite ? proposed.width : minimumSize.width
        let safeHeight = proposed.height.isFinite ? proposed.height : minimumSize.height
        let width = min(maximumSize.width, max(minimumSize.width, safeWidth))
        let height = min(maximumSize.height, max(minimumSize.height, safeHeight))
        return CGSize(width: width.rounded(), height: height.rounded())
    }
}
