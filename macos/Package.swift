// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "FluentNotepadMac",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/swiftlang/swift-markdown.git", branch: "main"),
    ],
    targets: [
        .executableTarget(
            name: "FluentNotepad",
            dependencies: [.product(name: "Markdown", package: "swift-markdown")],
            path: "Sources/FluentNotepad",
            exclude: ["Resources"]
        ),
    ]
)
