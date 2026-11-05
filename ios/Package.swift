// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "TuciCore",
    platforms: [.iOS(.v17), .macOS(.v13)],
    products: [.library(name: "TuciCore", targets: ["TuciCore"])],
    targets: [.target(name: "TuciCore"), .testTarget(name: "TuciCoreTests", dependencies: ["TuciCore"])]
)
