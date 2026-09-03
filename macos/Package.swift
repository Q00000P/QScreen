// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "QScreen",
    platforms: [.macOS(.v13)],
    dependencies: [
        .package(url: "https://github.com/sindresorhus/KeyboardShortcuts", from: "1.16.0")
    ],
    targets: [
        .executableTarget(
            name: "QScreen",
            dependencies: [
                .product(name: "KeyboardShortcuts", package: "KeyboardShortcuts")
            ],
            path: "Sources"
        )
    ]
)
