using System.Windows;
using SpotifyTaskbarWidget.Spotify;

namespace SpotifyTaskbarWidget.Views;

/// <summary>
/// Code-behind for the OAuth authentication window.
/// Shows on first launch or when re-authentication is needed.
/// </summary>
public partial class AuthWindow : Window
{
    private readonly SpotifyAuth _auth;
    private TokenData? _resultTokens;

    /// <summary>
    /// The tokens obtained from authentication, or null if cancelled/failed.
    /// </summary>
    public TokenData? ResultTokens => _resultTokens;

    public AuthWindow(SpotifyAuth auth)
    {
        InitializeComponent();
        _auth = auth;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ConnectButtonText.Text = "Waiting for browser...";
        StatusText.Text = "A browser window will open. Please log in to Spotify and authorize the app.";

        try
        {
            var tokens = await _auth.LoginAsync();

            if (tokens != null)
            {
                _resultTokens = tokens;
                StatusText.Text = "✓ Connected successfully!";

                // Brief delay to show success message
                await Task.Delay(800);
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "Authentication failed or was cancelled. Please try again.";
                ConnectButton.IsEnabled = true;
                ConnectButtonText.Text = "Connect to Spotify";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            ConnectButton.IsEnabled = true;
            ConnectButtonText.Text = "Connect to Spotify";
        }
    }
}
