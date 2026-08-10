import Foundation

/// Shared geometry rules for the floating desktop panel.  Keeping these values
/// in one place prevents restored frames and live resizing from drifting apart.
enum WidgetWindowGeometry {
    static let designSize = CGSize(width: 320, height: 347)
    static let minimumSize = designSize
    static let maximumSize = CGSize(width: 576, height: 625)
    static let aspectRatio = designSize.width / designSize.height

    static func constrainedSize(_ proposed: CGSize, relativeTo current: CGSize? = nil) -> CGSize {
        let widthChange = current.map { abs(proposed.width - $0.width) } ?? .infinity
        let heightChange = current.map { abs(proposed.height - $0.height) } ?? 0
        let prefersHeight = heightChange > widthChange
        let rawWidth = prefersHeight ? proposed.height * aspectRatio : proposed.width
        let safeWidth = rawWidth.isFinite ? rawWidth : minimumSize.width
        let width = min(maximumSize.width, max(minimumSize.width, safeWidth))
        return CGSize(width: width.rounded(), height: (width / aspectRatio).rounded())
    }
}
