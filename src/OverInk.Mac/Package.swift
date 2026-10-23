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
            dependencies: ["OverInkCore"],
            resources: [
                // Symlink to shared/remote-web: the iPad web client served by the remote server.
                .copy("RemoteWeb")
            ]
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
