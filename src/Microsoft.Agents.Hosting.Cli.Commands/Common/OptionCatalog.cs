using System.Collections.Frozen;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

/// <summary>
/// The option vocabulary, declared once. Owns each option's name, wire type and short alias;
/// the command supplies the description and whether it is required.
///
/// Aliases are declared literally rather than derived. The derivation algorithm they replace left
/// 17 of 62 options with none because their initials collided — <c>--scope</c>, <c>--sku</c> and
/// <c>--subscription</c> all wanted <c>-s</c>, so none of the three got one.
/// </summary>
public static class OptionCatalog
{
    static OptionCatalog()
    {
        if (Aliases.GroupBy(entry => entry.Value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            throw new CommandConfigurationException(
                $"Alias '{duplicate.Key}' is declared for {string.Join(" and ", duplicate.Select(entry => entry.Key))}. Each alias must name exactly one option.");
        }
    }

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["--account"] = "-acc",
        ["--agent-id"] = "-aid",
        ["--agent-harness"] = "-ah",
        ["--agent-name"] = "-an",
        ["--agent-type"] = "-at",
        ["--app-id"] = "-app",
        ["--app-object-id"] = "-aoid",
        ["--async"] = "-as",
        ["--auth-type"] = "-aut",
        ["--bot-name"] = "-bn",
        ["--by-reference"] = "-bref",
        ["--client-id"] = "-clid",
        ["--client-secret"] = "-clsec",
        ["--cloud"] = "-ci",
        ["--connection"] = "-con",
        ["--connection-id"] = "-conid",
        ["--connection-only"] = "-conly",
        ["--connection-reference"] = "-cref",
        ["--conversation-id"] = "-cvid",
        ["--connector"] = "-conn",
        ["--conversation"] = "-conv",
        ["--commit-id"] = "-cid",
        ["--config-format"] = "-cf",
        ["--count"] = "-cnt",
        ["--default"] = "-def",
        ["--debug"] = "-dbg",
        ["--delete-deployed-mcs-agent"] = "-ddma",
        ["--deployment-id"] = "-did",
        ["--deployment-name"] = "-dn",
        ["--deployment-type"] = "-dt",
        ["--description"] = "-d",
        ["--device-code"] = "-dc",
        ["--display-name"] = "-disp",
        ["--dry-run"] = "-dr",
        ["--endpoint"] = "-e",
        ["--endpoint-url"] = "-eu",
        ["--entra-id"] = "-ent",        
        ["--entra-id-type"] = "-eit",
        ["--environment-id"] = "-eid",
        ["--filter"] = "-flt",
        ["--force"] = "-f",
        ["--from-csv"] = "-csv",
        ["--generate"] = "-gen",
        ["--idempotency-key"] = "-ik",
        ["--install-playground"] = "-ipg",
        ["--json"] = "-j",
        ["--identity-mode"] = "-im",
        ["--init-template"] = "-it",
        ["--initial-prompt"] = "-ip",
        ["--language"] = "-lang",
        ["--language-version"] = "-lv",
        ["--limit"] = "-lim",
        ["--location"] = "-loc",
        ["--managed-identity-name"] = "-min",
        ["--mcp-discovery"] = "-md",
        ["--mcs-agent-source"] = "-mas",
        ["--mcs-agent-type"] = "-mat",
        ["--mcs-clone-agent-id"] = "-mcaid",
        ["--mcs-clone-agent-schemaname"] = "-mcas",
        ["--mcs-clone-environment-id"] = "-mceid",
        ["--mcs-clone-workflow-id"] = "-mcwid",
        ["--mcs-workflow-source"] = "-mws",
        ["--msa-app-id"] = "-maid",
        ["--name"] = "-n",
        ["--package"] = "-pkg",
        ["--package-revision"] = "-pr",
        ["--new-name"] = "-nn",
        ["--no-color"] = "-nc",
        ["--non-interactive"] = "-ni",
        ["--oauth"] = "-oa",
        ["--object-id"] = "-oid",
        ["--observability"] = "-obs",
        ["--output"] = "-o",
        ["--overwrite"] = "-ow",
        ["--parameters"] = "-par",
        ["--plain"] = "-pl",
        ["--project"] = "-p",
        ["--provider-name"] = "-pn",
        ["--publish"] = "-pub",
        ["--query"] = "-q",
        ["--published"] = "-pubd",
        ["--redirect-uri"] = "-ru",
        ["--replace"] = "-rep",
        ["--resource"] = "-r",
        ["--resource-group"] = "-rg",
        ["--role"] = "-rl",
        ["--run-id"] = "-rid",
        ["--schema-name"] = "-sn",
        ["--share-with-org"] = "-swo",
        ["--scope"] = "-s",
        ["--scopes"] = "-scp",
        ["--sku"] = "-sku",
        ["--subscription"] = "-sub",
        ["--teams-sso"] = "-ts",
        ["--target"] = "-tgt",
        ["--target-environment-id"] = "-tenv",
        ["--tenant"] = "-t",
        ["--tenant-wide"] = "-tw",
        ["--testset"] = "-tset",
        ["--transcript"] = "-tr",
        ["--timeout-minutes"] = "-tmo",
        ["--turns"] = "-trn",
        ["--update-testset"] = "-uts",
        ["--user"] = "-u",
        ["--version"] = "-v",
        ["--watch"] = "-w",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every option name the catalog knows, with its declared short alias.</summary>
    public static IReadOnlyDictionary<string, string> DeclaredAliases => Aliases;

    /// <summary>Creates a string-valued option.</summary>
    /// <param name="name">The option name, including leading dashes.</param>
    /// <param name="description">Command-specific help text.</param>
    /// <param name="required">Whether the parser requires a value.</param>
    public static CommandOption Value(string name, string description, bool required = false) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(string), IsRequired = required };

    /// <summary>Creates a bare flag.</summary>
    /// <param name="name">The option name, including leading dashes.</param>
    /// <param name="description">Command-specific help text.</param>
    public static CommandOption Switch(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(bool), IsSwitch = true };

    /// <summary>Creates an option every descendant of the owning command also accepts.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="description">Help text.</param>
    public static CommandOption Recursive(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(string), Recursive = true };

    /// <summary>Creates a flag every descendant of the owning command also accepts.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="description">Help text.</param>
    public static CommandOption RecursiveSwitch(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(bool), IsSwitch = true, Recursive = true };

    /// <summary>Creates a flag that distinguishes "not supplied" from false.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="description">Help text.</param>
    public static CommandOption OptionalSwitch(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(bool?), IsSwitch = true };

    /// <summary>Creates a nullable-integer option.</summary>
    /// <param name="name">The option name, including leading dashes.</param>
    /// <param name="description">Command-specific help text.</param>
    public static CommandOption Integer(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(int?) };

    /// <summary>Creates a repeatable string option.</summary>
    /// <param name="name">The option name, including leading dashes.</param>
    /// <param name="description">Command-specific help text.</param>
    public static CommandOption Strings(string name, string description) =>
        new(name, description) { ShortAlias = Alias(name), ValueType = typeof(string[]) };

    /// <summary>Creates a positional argument.</summary>
    /// <param name="name">The argument name as shown in usage.</param>
    /// <param name="description">Command-specific help text.</param>
    public static CommandArgument Argument(string name, string description) => new(name, description);

    /// <summary>The declared short alias for an option.</summary>
    /// <param name="name">The option name.</param>
    public static string Alias(string name) => Aliases.TryGetValue(name, out var alias)
        ? alias
        : throw new CommandConfigurationException($"Option '{name}' has no entry in OptionCatalog. Add its alias there so one name means one alias everywhere.");
}
