using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Feil.ViewModels;

namespace Feil;

/// <summary>
/// Given a view model, resolves the corresponding view.
/// Handles both the top-level ViewModel → View mapping and the
/// Pages sub-namespace (ViewModels/Pages/ → Views/Pages/).
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        string fullName = param.GetType().FullName!;

        // Feil.ViewModels.Pages.QueuePageViewModel  →  Feil.Views.Pages.QueuePage
        // Feil.ViewModels.MainWindowViewModel        →  Feil.Views.MainWindow
        string viewName = fullName
            .Replace("Feil.ViewModels.Pages.", "Feil.Views.Pages.", StringComparison.Ordinal)
            .Replace("Feil.ViewModels.", "Feil.Views.", StringComparison.Ordinal)
            .Replace("ViewModel", string.Empty, StringComparison.Ordinal);

        Type? type = Type.GetType(viewName);
        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = $"View not found: {viewName}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
