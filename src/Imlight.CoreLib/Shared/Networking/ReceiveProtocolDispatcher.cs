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
 *
 * ========================================================================
 * RECEIVE PROTOCOL DISPATCHER
 * ========================================================================
 * 
 * PURPOSE:
 * Provides a flexible message handling mechanism for network protocol 
 * dispatching using method attributes to route incoming messages.
 * 
 * USAGE EXAMPLE:
 * // Define a message handler method
 * [MessageHandler(typeof(SomeMessageType))]
 * private void HandleSomeMessage(SomeMessageType message) { }
 * 
 * NOTE:
 * - Extends Akka.NET ReceiveActor with dynamic message routing
 * - Uses reflection to map message types to handler methods
 * - Supports automatic message handler discovery
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Akka.Actor;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Networking;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class MessageHandlerAttribute(Type messageType) : Attribute {

    public Type MessageType { get; } = messageType;
    
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class InternalMessageHandlerAttribute(Type messageType) : MessageHandlerAttribute(messageType) { }

/// <summary>
/// An extension of a ReceiveActor that allows for receiving INetworkRecords directly to method attributes.
/// </summary>
public class ReceiveProtocolDispatcher : ReceiveActor {

    public Dictionary<Type, MethodInfo> MessageHandlers { get; private set; }

    protected ReceiveProtocolDispatcher() {
        MessageHandlers = MessageHandlerTable.HandlersOf(GetType());
        ConfigureReceivers();
        OnDispatcherConstructed();
    }

    /// <summary>
    /// CLASSIC: runs at the end of this constructor, inside the actor's context (Self is set). A derived class's field
    /// and primary-constructor initializers have run by then; its constructor body has not.
    /// </summary>
    protected virtual void OnDispatcherConstructed() { }

    /// <summary>
    /// CLASSIC: a handler threw. Return true when the receiver has dealt with it (logged it and acted); false lets it
    /// reach the supervisor as before. Runs inside the exception filter, before any finally block.
    /// </summary>
    protected virtual bool OnHandlerFault(object message, Exception exception) => false;

    /// <summary>
    /// CLASSIC: a client message that reached a service with no handler for it is warned about once per message type per
    /// process (it is a spinner or a hang for the player); server-internal messages stay with Akka's default. Only a
    /// session's services count: zone entities are also told the zone's broadcasts (combat, aggro, objects) and ignore
    /// the ones they don't need, which is not a missing handler.
    /// </summary>
    protected override void Unhandled(object message) {
        if (this is not MessageService) {
            base.Unhandled(message);
            return;
        }

        if (message is Imcodec.MessageLayer.IMessage and not IServerMessage && Classic.UnhandledMessageLog.FirstTime(message.GetType())) {
            Logger.Warning("{Actor} received client message {Type} and has no handler for it (first of its type; later ones at Verbose).",
                Logger.Args(GetType().Name, message.GetType().Name));
        }
        else if (message is Imcodec.MessageLayer.IMessage and not IServerMessage) {
            Logger.Verbose("{Actor} received unhandled client message {Type}.", Logger.Args(GetType().Name, message.GetType().Name));
        }

        base.Unhandled(message);
    }

    protected virtual void ConfigureReceivers() => Receive<object>(message => {
        var handler = MessageHandlerTable.DispatcherFor(GetType(), message.GetType());
        if (handler is null) {
            Unhandled(message);
            return;
        }

        // CLASSIC: handler times for the PERF log ([Classic] PerfLogSeconds); free when it is off.
        var started = Classic.PerfMonitor.Begin();
        try {
            handler(this, message);
        }
        catch (Exception ex) when (OnHandlerFault(message, ex)) {
            // CLASSIC: handled by the receiver (a session service closes its session; MessageService.OnHandlerFault).
        }
        finally {
            // CLASSIC: a component's own message is named by the component and what it got, not the envelope.
            if (message is Game.Zone.Core.ComponentMessage envelope) {
                Classic.PerfMonitor.EndHandler(envelope.Component?.GetType() ?? GetType(), envelope.Message?.GetType() ?? message.GetType(), started);
            }
            else {
                Classic.PerfMonitor.EndHandler(GetType(), message.GetType(), started);
            }
        }
    });

}
