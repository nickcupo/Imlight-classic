/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands;

internal abstract class CommandProtocol {

    internal abstract string Group { get; set; }
    protected CommandContext Context;

    private Dictionary<string, MethodInfo> _commandMethods;
    private bool _hasInitiated;

    internal bool Execute(string commandName, CommandContext context, params object[] parameters) {
        // If we haven't initiated the commands, do so now.
        if (!_hasInitiated) {
            InitiateHandlers();
        }

        this.Context = context;

        if (commandName is "help" or "?" or "" or null && !string.IsNullOrEmpty(Group)) {
            InformClientHelp(parameters.Length > 0 ? parameters[0]?.ToString() : null);
            return true;
        }

        if (!_commandMethods.TryGetValue(commandName.ToLower(), out var method)) {
            if (!string.IsNullOrEmpty(Group)) {
                // We don't need to log here if this is an ungrouped command. The command dispatcher is just
                // firing everywhere.
                Logger.Warning("Command {0} not found in {1}", Logger.Args(commandName, GetType().Name));
            }
            return false;
        }

        var authAttribute = method.GetCustomAttribute<AuthRequiredAttribute>();
        if (authAttribute != null) {
            var actualAuthLevel = authAttribute.Level;
#if !DEBUG
            // If the auth level is developer, bump it up to administrator if we're not in debug builds.
            if (actualAuthLevel == AuthLevel.Developer) {
                actualAuthLevel = AuthLevel.Administrator;
            }
#endif

            if (!AuthorityRequester.RequestAuthority(actualAuthLevel, context.Account, $"Command {commandName}")) {
                InformSenderClient("You do not have permission to use this command.");
                return true;
            }
        }

        // Process the parameters with respect to RemainderAttribute, if it is present.
        var methodParameters = method.GetParameters();
        var expectedParameterCount = methodParameters.Length;
        if (expectedParameterCount > 0) {
            // If we expected parameters, but none were provided, return.
            if (parameters.Length == 0) {
                InformClientOfProperParameterCount(commandName, methodParameters);
                return true;
            }

            var processedParameters = new List<object>();
            for (int i = 0; i < methodParameters.Length; i++) {
                if (methodParameters[i].GetCustomAttribute<RemainderAttribute>() != null) {
                    // Combine the remaining parameters into a single string
                    var restOfString = string.Join(" ", parameters.Skip(i));
                    processedParameters.Add(restOfString);

                    break;
                }
                else {
                    processedParameters.Add(parameters[i]);
                }
            }

            // If the parameter count doesn't match, return.
            if (expectedParameterCount != processedParameters.Count) {
                InformClientOfProperParameterCount(commandName, methodParameters);
                return true;
            }

            method.Invoke(this, [.. processedParameters]);
            return true;
        }
        else {
            // Invoke the method with no parameters.
            method.Invoke(this, null);
            return true;
        }
    }

    protected void InformSenderClient(string reason, bool isImportant = false)
        => Context.SessionActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
            Message = reason,
            Modal = (byte) (isImportant ? 1 : 0)
        });

    private void InitiateHandlers() {
        _hasInitiated = true;
        _commandMethods = new Dictionary<string, MethodInfo>();

        // Get all the methods in this class that have the Command attribute.
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in GetType().GetMethods(bindingFlags)) {
            var commandAttribute = method.GetCustomAttribute<CommandAttribute>();
            if (commandAttribute != null) {
                _commandMethods[commandAttribute.Name.ToLower()] = method;
            }

            // Add an entry for each of the aliases on the Alias attribute, if it has one.
            var aliasAttribute = method.GetCustomAttribute<AliasAttribute>();
            if (aliasAttribute != null) {
                foreach (var alias in aliasAttribute.Aliases) {
                    _commandMethods[alias.ToLower()] = method;
                }
            }
        }
    }

    private void InformClientOfProperParameterCount(string commandName, ParameterInfo[] methodParameters) {
        // Write the usage of this command.
        var properUsageStr = new StringBuilder();
        properUsageStr.Append($".{Group} {commandName}");

        foreach (var parameter in methodParameters) {
            properUsageStr.Append(" <");
            properUsageStr.Append(parameter.Name);

            if (parameter.GetCustomAttribute<RemainderAttribute>() != null) {
                properUsageStr.Append("...");
            }

            properUsageStr.Append('>');
        }

        // Inform the invoker of improper usage. Point and laugh!
        InformSenderClient($"Proper usage: {properUsageStr}");
    }

    private const int HelpNamesPerLine = 6;

    private void InformClientHelp(string? commandName) {
        // CLASSIC: the whole list in one window ran off the screen. ".mod help" lists the names, a few per line;
        // ".mod help gold" shows one command's usage and description.
        string Usage(MethodInfo method) {
            var parameters = string.Concat(method.GetParameters().Select(p => $" <{p.Name}>"));
            var help = method.GetCustomAttribute<HelpAttribute>()?.Text;
            return $".{Group} {method.GetCustomAttribute<CommandAttribute>()!.Name}{parameters}" + (help is null ? "" : $"\n{help}");
        }

        if (!string.IsNullOrWhiteSpace(commandName)) {
            InformSenderClient(_commandMethods.TryGetValue(commandName.ToLower(), out var method)
                ? Usage(method)
                : $"There is no .{Group} {commandName} command. Type .{Group} help for the list.", true);
            return;
        }

        var names = _commandMethods.Values.Distinct()
            .Select(method => method.GetCustomAttribute<CommandAttribute>()!.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder().AppendLine($".{Group} commands:");
        for (var i = 0; i < names.Count; i += HelpNamesPerLine) {
            sb.AppendLine(string.Join(",  ", names.Skip(i).Take(HelpNamesPerLine)));
        }
        sb.Append($"Type .{Group} help <command> for how to use one.");

        InformSenderClient(sb.ToString(), true);
    }

}
