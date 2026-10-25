namespace Teammy.Presentation;

public sealed record LaunchOptions(Uri? ApiAddress, bool DemoAuthentication, string? SmokeTestPath)
{
    public static LaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        Uri? apiAddress = null;
        var apiDemo = false;
        var demoSpecified = false;
        string? smokeTestPath = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--demo":
                    demoSpecified = true;
                    break;
                case "--api":
                case "--api-demo":
                    if (apiAddress is not null || ++index >= arguments.Count ||
                        !Uri.TryCreate(arguments[index], UriKind.Absolute, out apiAddress))
                    {
                        throw new ArgumentException("Provide one service URL after --api or --api-demo.");
                    }

                    apiDemo = argument == "--api-demo";
                    break;
                case "--smoke-test":
                    if (smokeTestPath is not null || ++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    {
                        throw new ArgumentException("Provide a PNG output path after --smoke-test.");
                    }

                    smokeTestPath = arguments[index];
                    break;
                default:
                    throw new ArgumentException("Unknown option. Use --demo, --api <https-url>, --api-demo <loopback-url>, or --smoke-test <png-path>.");
            }
        }

        if (apiAddress is not null && (demoSpecified || smokeTestPath is not null))
        {
            throw new ArgumentException("Local demo and screenshot smoke tests cannot connect to an API.");
        }

        return new LaunchOptions(apiAddress, apiDemo, smokeTestPath);
    }
}
