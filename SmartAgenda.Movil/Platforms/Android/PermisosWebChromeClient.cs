using Android.OS;
using Android.Webkit;
using WebView = Android.Webkit.WebView;

namespace SmartAgenda.Movil.Plataforma;

/// <summary>
/// Envuelve el "chrome client" que pone Blazor en el WebView para conceder el micrófono al
/// dictado por voz: cuando la página pide grabar audio, se pide el permiso de Android (la primera
/// vez sale el diálogo del sistema) y, si se concede, se le da a la página. Todo lo demás se
/// delega en el cliente original de Blazor.
/// </summary>
public class PermisosWebChromeClient(WebChromeClient original) : WebChromeClient
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null) return;

        var recursos = request.GetResources() ?? [];
        if (!recursos.Contains(PermissionRequest.ResourceAudioCapture))
        {
            request.Deny();
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var estado = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (estado != PermissionStatus.Granted)
            {
                estado = await Permissions.RequestAsync<Permissions.Microphone>();
            }

            if (estado == PermissionStatus.Granted)
            {
                request.Grant([PermissionRequest.ResourceAudioCapture]);
            }
            else
            {
                request.Deny();
            }
        });
    }

    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg) =>
        original.OnCreateWindow(view, isDialog, isUserGesture, resultMsg);

    public override void OnCloseWindow(WebView? window) => original.OnCloseWindow(window);

    public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) =>
        original.OnShowFileChooser(webView, filePathCallback, fileChooserParams);
}
