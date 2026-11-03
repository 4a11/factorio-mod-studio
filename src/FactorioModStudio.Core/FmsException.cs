namespace FactorioModStudio.Core;

/// <summary>An expected, user-facing failure (bad config, missing file, ...). The CLI prints the message without a stack trace.</summary>
public class FmsException : Exception
{
    public FmsException(string message) : base(message) { }
    public FmsException(string message, Exception inner) : base(message, inner) { }
}
