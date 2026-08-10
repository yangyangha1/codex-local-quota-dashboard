// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexQuotaWidget",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CodexQuotaWidget", targets: ["CodexQuotaWidget"])
    ],
    targets: [
        .executableTarget(
            name: "CodexQuotaWidget",
            path: "Sources/CodexQuotaWidget"
        )
    ]
)
