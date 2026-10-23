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
    dependencies: [
        // In-app updates. Only the app target uses it; OverInkCore stays free of it.
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.6.0")
    ],
    targets: [
        .target(name: "OverInkCore"),
        .executableTarget(
            name: "OverInkMac",
            dependencies: [
                "OverInkCore",
                .product(name: "Sparkle", package: "Sparkle")
            ],
            resources: [
                // Symlink to shared/remote-web: the iPad web client served by the remote server.
                .copy("RemoteWeb")
            ],
            linkerSettings: [
                // Inside the app bundle, Sparkle.framework sits in Contents/Frameworks.
                .unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])
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
