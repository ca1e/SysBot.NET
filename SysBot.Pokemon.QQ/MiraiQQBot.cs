using Mirai.Net.Data.Messages.Concretes;
using Mirai.Net.Data.Messages.Receivers;
using Mirai.Net.Sessions;
using Mirai.Net.Sessions.Http.Managers;
using Newtonsoft.Json;
using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Mirai.Net.Utils.Scaffolds;

namespace SysBot.Pokemon.QQ
{
    public class MiraiQQBot<T> where T : PKM, new()
    {
        private static PokeTradeHub<T> Hub = default!;

        internal static TradeQueueInfo<T> Info => Hub.Queues.Info;
        internal static readonly List<MiraiQQQueue<T>> QueuePool = new();
        private readonly MiraiBot client;
        private readonly string GroupId;
        private readonly QQSettings Settings;

        public MiraiQQBot(QQSettings settings, PokeTradeHub<T> hub)
        {
            Hub = hub;
            Settings = settings;

            client = new MiraiBot
            {
                Address = settings.Address,
                QQ = settings.QQ,
                VerifyKey = settings.VerifyKey
            };
            GroupId = settings.GroupId;
            client.MessageReceived.OfType<GroupMessageReceiver>()
                .Subscribe(async receiver =>
                {
                    var senderQQ = receiver.Sender.Id;
                    var groupId = receiver.Sender.Group.Id;
                    if (groupId != GroupId || senderQQ == client.QQ)
                        return;

                    if (settings.AliveMsg == receiver.MessageChain.OfType<PlainMessage>()?.FirstOrDefault()?.Text)
                    {
                        await MessageManager.SendGroupMessageAsync(groupId, settings.AliveMsg);
                        return;
                    }

                    if (await HandleFileUpload(receiver))
                    {
                        return;
                    }

                    await HandleCommand(receiver);
                });
        }

        public void StartingDistribution()
        {
            Task.Run(async () =>
            {
                await client.LaunchAsync();
                if (!string.IsNullOrWhiteSpace(Settings.MessageStart))
                {
                    await MessageManager.SendGroupMessageAsync(GroupId, Settings.MessageStart);
                    await Task.Delay(1_000).ConfigureAwait(false);
                }

                if (typeof(T) == typeof(PK8))
                {
                    await MessageManager.SendGroupMessageAsync(GroupId, "当前版本为剑盾");
                }
                else if (typeof(T) == typeof(PB8))
                {
                    await MessageManager.SendGroupMessageAsync(GroupId, "当前版本为晶灿钻石明亮珍珠");
                }
                else if (typeof(T) == typeof(PA8))
                {
                    await MessageManager.SendGroupMessageAsync(GroupId, "当前版本为阿尔宙斯");
                }

                await Task.Delay(1_000).ConfigureAwait(false);
            });
        }

        // todo: revise
        private async Task<bool> HandleFileUpload(GroupMessageReceiver receiver)
        {
            var senderQQ = receiver.Sender.Id;
            var groupId = receiver.Sender.Group.Id;
            var result = false;

            var fileMessages = receiver.MessageChain.OfType<FileMessage>();

            if (fileMessages.Any())
            {
                LogUtil.LogText("In file module");
                var file = fileMessages.First();
                var fileName = file.Name;
                string operationType;
                if (typeof(T) == typeof(PK8) &&
                    fileName.EndsWith(".pk8", StringComparison.OrdinalIgnoreCase)) operationType = "pk8";
                else if (typeof(T) == typeof(PB8) &&
                         fileName.EndsWith(".pb8", StringComparison.OrdinalIgnoreCase))
                    operationType = "pb8";
                else if (typeof(T) == typeof(PA8) &&
                         fileName.EndsWith(".pa8", StringComparison.OrdinalIgnoreCase))
                    operationType = "pa8";
                else return result;

                PKM pkm;
                try
                {
                    var f = await FileManager.GetFileAsync(groupId, file.FileId, true);

                    string url = f.DownloadInfo.Url;
                    byte[] data = new System.Net.WebClient().DownloadData(url);
                    switch (operationType)
                    {
                        case "pk8" or "pb8" when data.Length != 344:
                            await MessageManager.SendGroupMessageAsync(groupId, "非法文件");
                            return result;
                        case "pa8" when data.Length != 376:
                            await MessageManager.SendGroupMessageAsync(groupId, "非法文件");
                            return result;
                    }

                    switch (operationType)
                    {
                        case "pk8":
                            pkm = new PK8(data);
                            break;
                        case "pb8":
                            pkm = new PB8(data);
                            break;
                        case "pa8":
                            pkm = new PA8(data);
                            break;
                        default: return result;
                    }

                    LogUtil.LogText($"operationType:{operationType}");
                    await FileManager.DeleteFileAsync(groupId, file.FileId);
                }
                catch (Exception ex)
                {
                    LogUtil.LogText(ex.ToString());
                    return result;
                }

                //MessageManager.SendGroupMessageAsync(groupId, receiver.Sender.Name + " 上传了 " + fileName + " 文件");
                var _ = MiraiQQCommandsHelper<T>.AddToWaitingList(pkm, receiver.Sender.Name,
                    ulong.Parse(senderQQ), out string msg);
                if (_)
                {
                    await GetUserFromQueueAndGenerateCodeToTrade(senderQQ);
                    return true;
                }
                else
                {
                    await MessageManager.SendGroupMessageAsync(groupId, new AtMessage(senderQQ).Append(" 宝可梦信息异常"));
                    return false;
                }
            }

            return false;
        }

        private async Task HandleCommand(GroupMessageReceiver receiver)
        {
            string qqMsg;
            try
            {
                qqMsg = receiver.MessageChain.OfType<PlainMessage>().First().Text;
            }
            catch
            {
                return;
            }

            LogUtil.LogText($"debug qqMsg:{qqMsg}");
            var split = qqMsg.Split('\n');
            string c = "";
            string args = "";
            string nickName = receiver.Sender.Name;
            string qq = receiver.Sender.Id;
            if (split.Length > 0)
            {
                c = split[0];
                args = qqMsg[(qqMsg.IndexOf('\n') + 1)..];
            }

            switch (c)
            {
                case "$trade":
                    var _ = MiraiQQCommandsHelper<T>.AddToWaitingList(args, nickName, ulong.Parse(qq), out string msg);
                    if (_)
                    {
                        await GetUserFromQueueAndGenerateCodeToTrade(qq);
                    }
                    else
                    {
                        await MessageManager.SendGroupMessageAsync(GroupId, new AtMessage(qq).Append(" 宝可梦信息异常"));
                    }

                    break;
            }
        }

        private async Task GetUserFromQueueAndGenerateCodeToTrade(string qq)
        {
            var user = QueuePool.FindLast(q => q.QQ == ulong.Parse(qq));

            //LogUtil.LogInfo("QueuePool.ToString" + QueuePool.ToString(), "debug");
            if (user == null)
                return;
            QueuePool.Remove(user);

            Random rnd = new();
            try
            {
                int code = rnd.Next(0, 9999_9999); //Util.ToInt32(msg);
                var _ = AddToTradeQueue(user.Pokemon, code, user.QQ, user.DisplayName, RequestSignificance.Favored,
                    PokeRoutineType.LinkTrade, out string message);
                if (!_)
                    await MessageManager.SendGroupMessageAsync(GroupId, new AtMessage(qq).Append(" 已在队列中"));
                else
                    await MessageManager.SendGroupMessageAsync(GroupId, new AtMessage(qq).Append(message));
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                LogUtil.LogSafe(ex, nameof(MiraiQQBot<T>));
                LogUtil.LogError($"{ex.Message}", nameof(MiraiQQBot<T>));
            }
        }

        private bool AddToTradeQueue(T pk, int code, ulong qq, string displayName, RequestSignificance sig,
            PokeRoutineType type, out string msg)
        {
            var userID = qq;
            var name = displayName;

            var trainer = new PokeTradeTrainerInfo(name, userID);
            var notifier = new MiraiQQTradeNotifier<T>(pk, trainer, code, name, GroupId);
            var tt = type == PokeRoutineType.SeedCheck ? PokeTradeType.Seed : PokeTradeType.Specific;
            var detail = new PokeTradeDetail<T>(pk, trainer, notifier, tt, code, sig == RequestSignificance.Favored);
            var trade = new TradeEntry<T>(detail, userID, type, name);

            var added = Info.AddToTradeQueue(trade, userID, sig == RequestSignificance.Owner);

            if (added == QueueResultAdd.AlreadyInQueue)
            {
                msg = $"@{name}: Sorry, you are already in the queue.";
                return false;
            }

            var position = Info.CheckPosition(userID, type);
            //msg = $"@{name}: Added to the {type} queue, unique ID: {detail.ID}. Current Position: {position.Position}";
            msg = $" 你在第{position.Position}位";

            var botct = Info.Hub.Bots.Count;
            if (position.Position > botct)
            {
                var eta = Info.Hub.Config.Queues.EstimateDelay(position.Position, botct);
                //msg += $". Estimated: {eta:F1} minutes.";
                msg += $", 需等待约{eta:F1}分钟";
            }

            return true;
        }
    }
}