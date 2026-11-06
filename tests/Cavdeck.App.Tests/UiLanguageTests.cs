using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Markup;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class UiLanguageTests
{
    [Fact]
    public void AppWindow_tracks_the_active_ui_language_for_cjk_font_fallback()
    {
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var texts = new LocalizationService();
                var window = new AppWindow();
                window.UseTexts(texts);

                var content = new TextBlock { Text = "设置 Settings" };
                window.Content = content;

                foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "de-DE" })
                {
                    texts.ChangeLanguage(language);
                    var expectedLanguage = XmlLanguage.GetLanguage(language).IetfLanguageTag;

                    Assert.Equal(expectedLanguage, window.Language.IetfLanguageTag);
                    Assert.Equal(expectedLanguage, content.Language.IetfLanguageTag);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
                CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUiCulture;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
