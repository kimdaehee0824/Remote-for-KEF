// swift-tools-version: 6.0
import PackageDescription

// macOS 14: @Observable, SettingsLink/openSettings, NSApp.activate().
// Core and driver also build on Linux so tests can run in CI without a Mac.
var targets: [Target] = [
    .target(name: "KefRemoteCore"),
    .target(
        name: "KefW2Driver",
        dependencies: platformCrypto(["KefRemoteCore"])
    ),
    .testTarget(name: "KefRemoteCoreTests", dependencies: ["KefRemoteCore"]),
    .testTarget(name: "KefW2DriverTests", dependencies: ["KefW2Driver", "KefRemoteCore"]),
]
var products: [Product] = [
    .library(name: "KefRemoteCore", targets: ["KefRemoteCore"]),
    .library(name: "KefW2Driver", targets: ["KefW2Driver"]),
]

#if os(macOS)
targets.append(.executableTarget(name: "KEFRemote", dependencies: ["KefRemoteCore", "KefW2Driver"]))
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
    targets: targets
)
