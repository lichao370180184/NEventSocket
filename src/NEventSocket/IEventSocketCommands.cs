﻿namespace NEventSocket
{
    using System;
    using System.Threading.Tasks;

    using NEventSocket.FreeSwitch;

    public interface IEventSocketCommands
    {
        Task<EventMessage> ExecuteAppAsync(string uuid, string appName, string appArg, bool eventLock = false);

        Task<ApiResponse> SendApiAsync(string command);

        Task<BackgroundJobResult> BgApi(string command, string arg = null, Guid? jobUUID = null);

        Task<CommandReply> SendCommandAsync(string command);
    }
}