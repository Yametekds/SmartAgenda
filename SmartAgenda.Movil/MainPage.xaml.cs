using Microsoft.AspNetCore.Components.WebView;

namespace SmartAgenda.Movil;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void AlInicializarWebView(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if ANDROID
        // El WebView de Android no deja usar el micrófono a la página salvo que la app lo conceda:
        // se envuelve su "chrome client" para pedir el permiso de Android y dárselo al dictado.
        e.WebView.SetWebChromeClient(new Plataforma.PermisosWebChromeClient(e.WebView.WebChromeClient!));
        e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
#endif
    }
}
