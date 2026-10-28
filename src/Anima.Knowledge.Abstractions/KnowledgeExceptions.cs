namespace Anima.Knowledge;

public class KnowledgeException : Exception
{
    public KnowledgeException()
    {
    }

    public KnowledgeException(string message)
        : base(message)
    {
    }

    public KnowledgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KnowledgeValidationException : KnowledgeException
{
    public KnowledgeValidationException()
    {
    }

    public KnowledgeValidationException(string message)
        : base(message)
    {
    }

    public KnowledgeValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KnowledgeNotFoundException : KnowledgeException
{
    public KnowledgeNotFoundException()
    {
    }

    public KnowledgeNotFoundException(string message)
        : base(message)
    {
    }

    public KnowledgeNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A vocabulary pack is malformed, or a pack conflicts with a fixed entry.</summary>
public sealed class KnowledgeVocabularyException : KnowledgeException
{
    public KnowledgeVocabularyException()
    {
    }

    public KnowledgeVocabularyException(string message)
        : base(message)
    {
    }

    public KnowledgeVocabularyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
