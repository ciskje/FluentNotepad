import SwiftUI

/// Native Settings window (⌘,).
struct SettingsView: View {
    @Bindable var store: Store

    var body: some View {
        Form {
            Section(L("appearance")) {
                Picker(L("appTheme"), selection: $store.theme)
                {
                    Text(L("light")).tag("light")
                    Text(L("dark")).tag("dark")
                    Text(L("followSystem")).tag("system")
                }
                .pickerStyle(.radioGroup)
            }
            Section(L("editor")) {
                Toggle(L("wordWrap"), isOn: $store.wrap)
                HStack {
                    Text(L("fontSize"))
                    Slider(value: $store.fontSize,
                        in: 12 ... 24, step: 1)
                    Text("\(Int(store.fontSize))").frame(width: 24)
                }
            }
            Section(L("about")) {
                Text("FluentNotepad \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?") — \(L("aboutPreview"))")
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 380)
        .padding()
    }
}
