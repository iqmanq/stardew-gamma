using System;
using StardewModdingAPI;

namespace Gamma;

/// <summary>
/// Minimal client for the Generic Mod Config Menu API (only the members this mod uses),
/// matched against the signatures of the API build installed with the game.
/// </summary>
public interface IGenericModConfigMenuApi
{
    // note: reset comes BEFORE save in this API version, and there is no open-menu callback
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void Unregister(IManifest mod);

    void AddSectionTitle(IManifest mod, Func<string> text, Func<string> tooltip = null);

    void AddParagraph(IManifest mod, Func<string> text);

    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name = null, Func<string> tooltip = null, string fieldId = null);

    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name = null, Func<string> tooltip = null,
        int? min = null, int? max = null, int? interval = null, Func<int, string> formatValue = null, string fieldId = null);

    void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name = null, Func<string> tooltip = null,
        float? min = null, float? max = null, float? interval = null, Func<float, string> formatValue = null, string fieldId = null);

    void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name = null, Func<string> tooltip = null,
        string[] allowedValues = null, Func<string, string> formatAllowedValue = null, string fieldId = null);

    void AddKeybind(IManifest mod, Func<SButton> getValue, Action<SButton> setValue, Func<string> name = null, Func<string> tooltip = null, string fieldId = null);

    void OnFieldChanged(IManifest mod, Action<string, object> onChange);
}
