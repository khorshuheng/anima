namespace Anima.Deploy;

public class DeployException(string message) : Exception(message);

public sealed class DeploySignatureException(string message) : DeployException(message);

public sealed class DeployValidationException(string message) : DeployException(message);
