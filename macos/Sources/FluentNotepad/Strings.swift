import Foundation

/// NSLocalizedString shorthand. Strings live in Resources/<lang>.lproj (copied
/// into the .app by the bundle script, no Xcode string catalog needed).
func L(_ key: String, _ args: CVarArg...) -> String {
    let f = NSLocalizedString(key, comment: "")
    return args.isEmpty ? f : String(format: f, arguments: args)
}
