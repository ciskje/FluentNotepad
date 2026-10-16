import SwiftUI

/// Native Settings window (⌘,).
struct SettingsView: View {
    @Bindable var store: Store

    var body: some View {
        Form {
            Section(L("appearance")) {
                Picker(L("appTheme"), selection: Binding(
                    get: { store.theme },
                    set: { store.theme = $0 }))
                {
                    Text(L("light")).tag("light")
                    Text(L("dark")).tag("dark")
                    Text(L("followSystem")).tag("system")
                }
                .pickerStyle(.radioGroup)
            }
            Section(L("editor")) {
                Toggle(L("wordWrap"), isOn: Binding(
                    get: { store.wrap }, set: { store.wrap = $0 }))
                HStack {
                    Text(L("fontSize"))
                    Slider(value: Binding(get: { store.fontSize }, set: { store.fontSize = $0 }),
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
