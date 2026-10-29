// swift-tools-version: 6.0
import PackageDescription

// macOS 14: @Observable, SettingsLink/openSettings, NSApp.activate().
// Core and driver also build on Linux so tests can run in CI without a Mac.
// The app's only dependency is Sparkle (updates), and only on macOS.
var targets: [Target] = [
    .target(name: "KefRemoteCore"),
    .target(
        name: "KefW2Driver",
        dependencies: platformCrypto(["KefRemoteCore"])
    ),
    .testTarget(name: "KefRemoteCoreTests", dependencies: ["KefRemoteCore"]),
    .testTarget(name: "KefW2DriverTests", dependencies: ["KefW2Driver", "KefRemoteCore"]),
]
var dependencies: [Package.Dependency] = []
var products: [Product] = [
    .library(name: "KefRemoteCore", targets: ["KefRemoteCore"]),
    .library(name: "KefW2Driver", targets: ["KefW2Driver"]),
]

#if os(macOS)
dependencies.append(.package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.10.1"))
targets.append(.executableTarget(
    name: "KEFRemote",
    dependencies: ["KefRemoteCore", "KefW2Driver", .product(name: "Sparkle", package: "Sparkle")]
))
products.append(.executable(name: "KEFRemote", targets: ["KEFRemote"]))
#else
// Apple platforms use CryptoKit and CommonCrypto. Elsewhere the system OpenSSL is used.
targets.append(.systemLibrary(name: "COpenSSL", pkgConfig: "openssl", providers: [.apt(["libssl-dev"])]))
#endif

func platformCrypto(_ deps: [Target.Dependency]) -> [Target.Dependency] {
    #if os(macOS)
    return deps
    #else
    return deps + ["COpenSSL"]
    #endif
}

let package = Package(
    name: "KEFRemote",
    platforms: [.macOS(.v14)],
    products: products,
    dependencies: dependencies,
    targets: targets
)
