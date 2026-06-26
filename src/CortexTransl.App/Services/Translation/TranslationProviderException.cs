namespace CortexTransl.App.Services.Translation;

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(string message, string providerStatus, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderStatus = providerStatus;
    }

    public string ProviderStatus { get; }
}
