// swift-tools-version: 6.2

import PackageDescription

let package = Package(
    name: "OverInkMac",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "OverInkCore", targets: ["OverInkCore"]),
        .executable(name: "OverInkMac", targets: ["OverInkMac"]),
        .executable(name: "OverInkCoreChecks", targets: ["OverInkCoreChecks"])
    ],
    targets: [
        .target(name: "OverInkCore"),
        .executableTarget(
            name: "OverInkMac",
            dependencies: ["OverInkCore"]
        ),
        .executableTarget(
            name: "OverInkCoreChecks",
            dependencies: ["OverInkCore"]
        ),
        .testTarget(
            name: "OverInkCoreTests",
            dependencies: ["OverInkCore"]
        )
    ]
)
