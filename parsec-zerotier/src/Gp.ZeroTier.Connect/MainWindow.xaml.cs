using System.Text.RegularExpressions;
using System.Net.Http;
using System.Windows;

namespace Gp.ZeroTier.Connect;

public partial class MainWindow : Window
{
    private static readonly Regex CodePattern = new("^[0-9]{3}-[0-9]{3}$", RegexOptions.CultureInvariant);
    private readonly ProvisioningService provisioning;

    public MainWindow()
    {
        InitializeComponent();
        var options = LauncherOptions.CreateDefault();
        var backend = new BackendClient(new HttpClient { BaseAddress = options.BackendBaseUri, Timeout = TimeSpan.FromSeconds(20) });
        var storage = new SecureStorage(options.StateDirectory);
        var telemetry = new TelemetryService(backend, storage);
        provisioning = new ProvisioningService(backend, telemetry, storage, new WindowsNetworkInspector(),
            new ZeroTierManager(options), new ParsecPortableManager(options));
        Loaded += async (_, _) => await CleanupPreviousLeaseAsync();
    }

    private async Task CleanupPreviousLeaseAsync()
    {
        SetConnectionControlsEnabled(false);
        StatusText.Text = "Sprawdzanie poprzedniego połączenia…";
        try
        {
            var result = await provisioning.CleanupExpiredStateAsync(CancellationToken.None);
            if (result is null)
            {
                StatusText.Text = "Wprowadź kod otrzymany po przydzieleniu maszyny.";
                SetConnectionControlsEnabled(true);
            }
            else
            {
                StatusText.Text = result.Message;
                SetConnectionControlsEnabled(!result.HasActiveLease);
            }
        }
        catch (LauncherException ex)
        {
            StatusText.Text = $"Nie można przygotować połączenia ({ex.Code}).\n{ex.Message}";
        }
        catch
        {
            StatusText.Text = "Nie udało się sprawdzić poprzedniego połączenia. Możesz spróbować ponownie.";
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var code = ActivationCode.Text.Trim();
        if (!CodePattern.IsMatch(code))
        {
            StatusText.Text = "Kod musi mieć format 123-456.";
            return;
        }
        ActivationCode.Clear();

        ConnectButton.IsEnabled = false;
        ActivationCode.IsEnabled = false;
        StatusText.Text = "Przygotowywanie połączenia…";
        var ready = false;
        try
        {
            var result = await provisioning.ProvisionAsync(code, CancellationToken.None);
            StatusText.Text = result.Message;
            ready = result.HasActiveLease;
        }
        catch (LauncherException ex)
        {
            StatusText.Text = $"Nie można przygotować połączenia ({ex.Code}).\n{ex.Message}";
        }
        catch
        {
            StatusText.Text = "Nieoczekiwany błąd aplikacji. Spróbuj ponownie lub przekaż administratorowi czas wystąpienia błędu.";
        }
        finally
        {
            SetConnectionControlsEnabled(!ready);
        }
    }

    private void SetConnectionControlsEnabled(bool enabled)
    {
        ConnectButton.IsEnabled = enabled;
        ActivationCode.IsEnabled = enabled;
    }
}
