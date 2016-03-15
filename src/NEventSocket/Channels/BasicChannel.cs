﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="BasicChannel.cs" company="Dan Barua">
//   (C) Dan Barua and contributors. Licensed under the Mozilla Public License.
// </copyright>
// <summary>
//   Defines the BasicChannel type.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace NEventSocket.Channels
{
    using System;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading.Tasks;

    using NEventSocket.FreeSwitch;
    using NEventSocket.Logging;
    using NEventSocket.Sockets;
    using NEventSocket.Util;

    public abstract class BasicChannel
    {
        protected readonly ILog Log;

        protected readonly CompositeDisposable Disposables = new CompositeDisposable();

        protected EventSocket eventSocket;

        protected EventMessage lastEvent;

        private Action<EventMessage> hangupCallback = (e) => { };

        private readonly InterlockedBoolean disposed = new InterlockedBoolean(false);

        ~BasicChannel()
        {
            Dispose(false);
        }

        protected BasicChannel(EventMessage eventMessage, EventSocket eventSocket)
        {
            Log = LogProvider.GetLogger(GetType());

            UUID = eventMessage.UUID;
            lastEvent = eventMessage;
            this.eventSocket = eventSocket;

            Disposables.Add(
                eventSocket.Events
                           .Where(x => x.UUID == UUID)
                           .Subscribe(
                               e =>
                                   {
                                       lastEvent = e;

                                       if (e.EventName == EventName.ChannelAnswer)
                                       {
                                           Log.Info(() => "Channel [{0}] Answered".Fmt(UUID));
                                       }

                                       if (e.EventName == EventName.ChannelHangup)
                                       {
                                           Log.Info(() => "Channel [{0}] Hangup Detected [{1}]".Fmt(UUID, e.HangupCause));
                                           Dispose();
                                           HangupCallBack(e);
                                       }
                                   }));
        }

        /// <summary>
        /// Provides access to the underlying <see cref="EventSocket"/> for low-level operations
        /// </summary>
        public AdvancedProperties Advanced { get { return new AdvancedProperties(this); } }

        public string UUID { get; protected set; }

        public ChannelState ChannelState
        {
            get
            {
                return lastEvent.ChannelState;
            }
        }

        public AnswerState? Answered
        {
            get
            {
                return lastEvent.AnswerState;
            }
        }

        public HangupCause? HangupCause
        {
            get
            {
                return lastEvent.HangupCause;
            }
        }

        public Action<EventMessage> HangupCallBack
        {
            get
            {
                return hangupCallback;
            }

            set
            {
                hangupCallback = value;
            }
        }

        public IObservable<string> Dtmf
        {
            get
            {
                return
                    eventSocket.Events.Where(x => x.UUID == UUID && x.EventName == EventName.Dtmf)
                        .Select(x => x.Headers[HeaderNames.DtmfDigit]);
            }
        }

        public bool IsBridged
        {
            get
            {
                return lastEvent != null && lastEvent.Headers.ContainsKey(HeaderNames.OtherLegUniqueId) && lastEvent.Headers[HeaderNames.OtherLegUniqueId] != null; //this.BridgedChannel != null; // 
            }
        }

        public bool IsAnswered
        {
            get
            {
                return Answered.HasValue && Answered.Value == AnswerState.Answered;
            }
        }

        public bool IsPreAnswered
        {
            get
            {
                return Answered.HasValue && Answered.Value == AnswerState.Early;
            }
        }

        public IObservable<string> FeatureCodes(string prefix = "#")
        {
            return eventSocket
                       .Events.Where(x => x.EventName == EventName.Dtmf && x.UUID == UUID).Select(x => x.Headers[HeaderNames.DtmfDigit])
                       .Buffer(TimeSpan.FromSeconds(2), 2)
                       .Where(x => x.Count == 2 && x[0] == prefix)
                       .Select(x => string.Concat(x))
                       .Do(x => Log.Debug(() => "Channel {0} detected Feature Code {1}".Fmt(UUID, x)));
        }

        public Task Hangup(HangupCause hangupCause = FreeSwitch.HangupCause.NormalClearing)
        {
            return
                RunIfAnswered(
                    () =>
                    eventSocket.SendApi("uuid_kill {0} {1}".Fmt(UUID, hangupCause.ToString().ToUpperWithUnderscores())),
                    true);
        }

        public async Task PlayFile(string file, Leg leg = Leg.ALeg, bool mix = false, string terminator = null)
        {
            if (!IsAnswered)
            {
                return;
            }

            if (terminator != null)
            {
                await SetChannelVariable("playback_terminators", terminator).ConfigureAwait(false);
            }

            if (leg == Leg.ALeg) //!this.IsBridged)
            {
                await eventSocket.Play(UUID, file, new PlayOptions()).ConfigureAwait(false);
                return;
            }

            // uuid displace only works on one leg
            switch (leg)
            {
                case Leg.Both:
                    await
                        Task.WhenAll(
                            eventSocket.ExecuteApplication(UUID, "displace_session", "{0} {1}{2}".Fmt(file, mix ? "m" : string.Empty, "w"), false, false),
                            eventSocket.ExecuteApplication(UUID, "displace_session", "{0} {1}{2}".Fmt(file, mix ? "m" : string.Empty, "r"), false, false))
                            .ConfigureAwait(false);
                    break;
                case Leg.ALeg:
                    await eventSocket.ExecuteApplication(UUID, "displace_session", "{0} {1}{2}".Fmt(file, mix ? "m" : string.Empty, "w"), false, false).ConfigureAwait(false);
                    break;
                case Leg.BLeg:
                    await eventSocket.ExecuteApplication(UUID, "displace_session", "{0} {1}{2}".Fmt(file, mix ? "m" : string.Empty, "r"), false, false).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException("Leg {0} is not supported".Fmt(leg));
            }
        }

        public async Task<string> PlayGetDigits(PlayGetDigitsOptions options)
        {
            if (!IsAnswered)
            {
                return string.Empty;
            }

            var result = await eventSocket.PlayGetDigits(UUID, options).ConfigureAwait(false);
            return result.Digits;
        }

        public Task<ReadResult> Read(ReadOptions options)
        {
            if (!IsAnswered)
            {
                return Task.FromResult(new ReadResult(null, null));
            }

            return eventSocket.Read(UUID, options);
        }

        public Task Say(SayOptions options)
        {
            return RunIfAnswered(() => eventSocket.Say(UUID, options));
        }

        /// <summary>
        /// Performs an attended transfer. If succeded, it will replace the Bridged Channel of the other Leg.
        /// </summary>
        /// <remarks>
        /// See https://freeswitch.org/confluence/display/FREESWITCH/Attended+Transfer
        /// </remarks>
        /// <param name="endpoint">The endpoint to transfer to eg. user/1000, sofia/foo@bar.com etc</param>
        public Task<AttendedTransferResult> AttendedTransfer(string endpoint)
        {
            try
            {
                var tcs = new TaskCompletionSource<AttendedTransferResult>();
                var subscriptions = new CompositeDisposable();

                var aLegUUID = lastEvent.Headers[HeaderNames.OtherLegUniqueId];
                var bLegUUID = UUID;

                var events = eventSocket.Events;

                Log.Debug(() => "Att XFer Starting A-Leg [{0}] B-Leg [{1}]".Fmt(aLegUUID, bLegUUID));

                var aLegHangup = events.Where(x => x.EventName == EventName.ChannelHangup && x.UUID == aLegUUID)
                                        .Do(x => Log.Debug(() => "Att XFer Hangup Detected on A-Leg [{0}]".Fmt(x.UUID)));

                var bLegHangup = events.Where(x => x.EventName == EventName.ChannelHangup && x.UUID == bLegUUID)
                                        .Do(x => Log.Debug(() => "Att XFer Hangup Detected on B-Leg [{0}]".Fmt(x.UUID)));

                var cLegHangup = events.Where(x => x.EventName == EventName.ChannelHangup && x.UUID != bLegUUID && x.UUID != aLegUUID)
                                        .Do(x => Log.Debug(() => "Att XFer Hangup Detected on C-Leg[{0}]".Fmt(x.UUID)));

                var cLegAnswer =
                    events.Where(x => x.EventName == EventName.ChannelAnswer && x.UUID != bLegUUID && x.UUID != aLegUUID)
                          .Do(x => Log.Debug(() => "Att XFer Answer Detected on C-Leg [{0}]".Fmt(x.UUID)));

                var bLegUnbridge =
                    events.Where(x => x.EventName == EventName.ChannelUnbridge && x.UUID == bLegUUID)
                          .Do(x => Log.Debug(() => "Att XFer Unbridge Detected on C-Leg [{0}]".Fmt(x.UUID)));

                var cLegUnbridge =
                    events.Where(x => x.EventName == EventName.ChannelUnbridge && x.UUID != bLegUUID && x.UUID != aLegUUID)
                          .Do(x => Log.Debug(() => "Att XFer Unbridge Detected on C-Leg [{0}]".Fmt(x.UUID)));

                var aLegBridge =
                    events.Where(x => x.EventName == EventName.ChannelBridge && x.UUID == aLegUUID)
                          .Do(x => Log.Debug(() => "Att XFer Bridge Detected on A-Leg [{0}]".Fmt(x.UUID)));

                var cLegBridge =
                    events.Where(x => x.EventName == EventName.ChannelBridge && x.UUID != bLegUUID && x.UUID != aLegUUID)
                          .Do(x => Log.Debug(() => "Att XFer Bridge Detected on C-Leg [{0}]".Fmt(x.UUID)));


                var channelExecuteComplete =
                    events.Where(
                        x =>
                            x.EventName == EventName.ChannelExecuteComplete
                            && x.UUID == bLegUUID
                            && x.GetHeader(HeaderNames.Application) == "att_xfer");

                var cNotAnswered = cLegHangup.And(channelExecuteComplete.Where(x => x.GetVariable("originate_disposition") == "NO_ANSWER"));

                var cRejected = cLegHangup.And(channelExecuteComplete.Where(x => x.GetVariable("originate_disposition") == "CALL_REJECTED"));

                var cAnsweredThenHungUp =
                    cLegAnswer.And(cLegHangup)
                        .And(channelExecuteComplete.Where(
                                x =>
                                    x.GetVariable("att_xfer_result") == "success"
                                    && x.GetVariable("last_bridge_hangup_cause") == "NORMAL_CLEARING"
                                    && x.GetVariable("originate_disposition") == "SUCCESS"));

                var cAnsweredThenBPressedStarOrHungUp =
                    cLegAnswer.And(bLegHangup)
                        .And(cLegBridge.Where(x => x.OtherLegUUID == aLegUUID));

                subscriptions.Add(Observable.When(cNotAnswered.Then((hangup, execComplete) => new { hangup, execComplete }))
                                            .Subscribe(
                                                x =>
                                                {
                                                    Log.Debug(() => "Att Xfer Not Answered");
                                                    tcs.TrySetResult(AttendedTransferResult.Failed(FreeSwitch.HangupCause.NoAnswer));
                                                }));

                subscriptions.Add(Observable.When(cRejected.Then((hangup, execComplete) => new {hangup, execComplete}))
                                            .Subscribe(x => {
                                                Log.Debug(() => "Att Xfer Rejected");
                                                tcs.TrySetResult(AttendedTransferResult.Failed(FreeSwitch.HangupCause.CallRejected));
                                            }));

                subscriptions.Add(Observable.When(cAnsweredThenHungUp.Then((answer, hangup, execComplete) => new { answer, hangup, execComplete }))
                                            .Subscribe(
                                                x =>
                                                {
                                                    Log.Debug(() => "Att Xfer Rejected after C Hungup");
                                                    tcs.TrySetResult(AttendedTransferResult.Failed(FreeSwitch.HangupCause.NormalClearing));
                                                }));

                subscriptions.Add(channelExecuteComplete.Where(x => !string.IsNullOrEmpty(x.GetVariable("xfer_uuids")))
                                            .Subscribe(x => {
                                                    Log.Debug(() => "Att Xfer Success (threeway)");
                                                    tcs.TrySetResult(AttendedTransferResult.Success(AttendedTransferResultStatus.Threeway));
                                                }));

                subscriptions.Add(Observable.When(cAnsweredThenBPressedStarOrHungUp.Then((answer, hangup, bridge) => new { answer, hangup, bridge }))
                                            .Subscribe(
                                                x =>
                                                {
                                                    Log.Debug(() => "Att Xfer Succeeded after B pressed *");
                                                    tcs.TrySetResult(AttendedTransferResult.Success());
                                                }));

                subscriptions.Add(Observable.When(bLegHangup.And(cLegAnswer).And(aLegBridge.Where(x => x.OtherLegUUID != bLegUUID)).Then((hangup, answer, bridge) => new { answer, hangup, bridge }))
                                            .Subscribe(
                                                x =>
                                                {
                                                    Log.Debug(() => "Att Xfer Succeeded after B hung up and C answered");
                                                    tcs.TrySetResult(AttendedTransferResult.Success());
                                                }));

                subscriptions.Add(aLegHangup.Subscribe(
                    x =>
                    {
                        Log.Debug(() => "Att Xfer Failed after A-Leg Hung Up");
                        tcs.TrySetResult(AttendedTransferResult.Hangup(x));
                    }));

                eventSocket.ExecuteApplication(UUID, "att_xfer", endpoint, false, true)
                           .ContinueOnFaultedOrCancelled(tcs, subscriptions.Dispose);

                return tcs.Task.Then(() => subscriptions.Dispose());
            }
            catch (TaskCanceledException ex)
            {
                return Task.FromResult(AttendedTransferResult.Failed(FreeSwitch.HangupCause.None));
            }
        }

        public async Task StartDetectingInbandDtmf()
        {
            if (!IsAnswered)
            {
                return;
            }

            await eventSocket.SubscribeEvents(EventName.Dtmf).ConfigureAwait(false);
            await eventSocket.StartDtmf(UUID).ConfigureAwait(false);
        }

        public Task StopDetectingInbandDtmf()
        {
            return RunIfAnswered(() => eventSocket.Stoptmf(UUID));
        }

        public Task SetChannelVariable(string name, string value)
        {
            if (!IsAnswered)
            {
                return TaskHelper.Completed;
            }

            Log.Debug(() => "Channel {0} setting variable '{1}' to '{2}'".Fmt(UUID, name, value));
            return eventSocket.SendApi("uuid_setvar {0} {1} {2}".Fmt(UUID, name, value));
        }

        /// <summary>
        /// Send DTMF digits to the channel
        /// </summary>
        /// <param name="digits">String with digits or characters</param>
        /// <param name="duration">Duration of each symbol (default -- 2000ms)</param>
        /// <returns></returns>
        public Task SendDTMF(string digits, TimeSpan? duration = null)
        {
            var durationMs = duration.HasValue ? duration.Value.TotalMilliseconds : 2000; // default value in freeswitch
            return this.eventSocket.ExecuteApplication(this.UUID, "send_dtmf", "{0}@{1}".Fmt(digits, durationMs));
        }

        public Task Exit()
        {
            return eventSocket.Exit();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed != null && disposed.EnsureCalledOnce())
            {
                if (disposing)
                {
                    if (Disposables != null)
                    {
                        Disposables.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Runs the given async function if the Channel is still connected, otherwise a completed Task.
        /// </summary>
        /// <param name="toRun">An Async function.</param>
        /// <param name="orPreAnswered">Function also run in pre answer state</param>
        protected Task RunIfAnswered(Func<Task> toRun, bool orPreAnswered = false)
        {
            if (!IsAnswered && (!orPreAnswered || !IsPreAnswered))
            {
                return TaskHelper.Completed;
            }

            return toRun();
        }

        public class AdvancedProperties
        {
            private BasicChannel channel;

            public AdvancedProperties(BasicChannel channel)
            {
                this.channel = channel;
            }

            public EventMessage LastEvent { get { return channel.lastEvent; } }

            public EventSocket Socket { get { return channel.eventSocket; } }

            public string GetHeader(string headerName)
            {
                return channel.lastEvent.GetHeader(headerName);
            }

            public string GetVariable(string variableName)
            {
                return channel.lastEvent.GetVariable(variableName);
            }
        }
    }
}