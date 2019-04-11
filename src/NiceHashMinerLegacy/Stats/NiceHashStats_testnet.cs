﻿// TESTNET
#if TESTNET || TESTNETDEV
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NiceHashMiner.Devices;
using NiceHashMiner.Miners;
using NiceHashMiner.Switching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using NiceHashMiner.Benchmarking;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Stats.Models;
using NiceHashMinerLegacy.Common.Enums;
using NiceHashMinerLegacy.Extensions;
using WebSocketSharp;
using NiceHashMiner.Configs;
// static imports
using static NiceHashMiner.Stats.StatusCodes;

namespace NiceHashMiner.Stats
{
    public class SocketEventArgs : EventArgs
    {
        public readonly string Message;

        public SocketEventArgs(string message)
        {
            Message = message;
        }
    }

    public static class NiceHashStats
    {
#region JSON Models
#pragma warning disable 649, IDE1006
        private class NicehashCredentials
        {
            public string method = "credentials.set";
            public string btc;
            public string worker;
        }

        private class NicehashDeviceStatus
        {
            public string method = "miner.status";
            [JsonProperty("params")]
            public List<JToken> param;
        }
        public class ExchangeRateJson
        {
            public List<Dictionary<string, string>> exchanges { get; set; }
            public Dictionary<string, double> exchanges_fiat { get; set; }
        }
#pragma warning restore 649, IDE1006
#endregion

        private const int DeviceUpdateLaunchDelay = 20 * 1000;
        private const int DeviceUpdateInterval = 45 * 1000;

        //public static double Balance { get; private set; }
        public static string Version { get; private set; }
        public static string VersionLink { get; private set; }
        public static bool IsAlive => _socket?.IsAlive ?? false;

        // Event handlers for socket
        public static event EventHandler OnSmaUpdate;
        public static event EventHandler OnConnectionLost;
        public static event EventHandler OnExchangeUpdate;
        public static event EventHandler<DeviceUpdateEventArgs> OnDeviceUpdate;

        private static NiceHashSocket _socket;
        
        private static System.Threading.Timer _deviceUpdateTimer;

        public static void StartConnection(string address)
        {
            if (_socket == null)
            {
                _socket = new NiceHashSocket(address);
                _socket.OnConnectionEstablished += SocketOnOnConnectionEstablished;
                _socket.OnDataReceived += SocketOnOnDataReceived;
                _socket.OnConnectionLost += SocketOnOnConnectionLost;
            }
            _socket.StartConnection(ConfigManager.GeneralConfig.BitcoinAddress, ConfigManager.GeneralConfig.WorkerName, ConfigManager.GeneralConfig.RigGroup);
            _deviceUpdateTimer = new System.Threading.Timer(MinerStatus_Tick, null, DeviceUpdateInterval, DeviceUpdateInterval);
        }

        public static void EndConnection()
        {
            _socket?.EndConnection();
        }

#region Socket Callbacks

        private static void SocketOnOnConnectionLost(object sender, EventArgs eventArgs)
        {
            OnConnectionLost?.Invoke(sender, eventArgs);
        }

        private static void SocketOnOnDataReceived(object sender, MessageEventArgs e)
        {
            ExecutedInfo info = null;
            var executed = false;
            int? id = null;
            try
            {
                if (e.IsText)
                {
                    info = ProcessData(e.Data, out executed, out id);
                }

                if (executed)
                {
                    SendExecuted(info, id);
                }
            }
            catch (RpcException rEr)
            {
                Helpers.ConsolePrint("SOCKET", rEr.ToString());
                if (!executed) return;
                Helpers.ConsolePrint("SOCKET", $"Sending executed response with code {rEr.Code}");
                SendExecuted(info, id, rEr.Code, rEr.Message);
            }
            catch (Exception er)
            {
                Helpers.ConsolePrint("SOCKET", er.ToString());
            }
        }

        internal static ExecutedInfo ProcessData(string data, out bool executed, out int? id)
        {
            Helpers.ConsolePrint("SOCKET", "Received: " + data);
            dynamic message = JsonConvert.DeserializeObject(data);
            executed = false;

            if (message == null)
                throw new RpcException("No message found", ErrorCode.UnableToHandleRpc);

            id = (int?) message.id;
            switch (message.method.Value)
            {
                case "sma":
                {
                    // Try in case stable is not sent, we still get updated paying rates
                    try
                    {
                        var stable = JsonConvert.DeserializeObject(message.stable.Value);
                        SetStableAlgorithms(stable);
                    }
                    catch
                    { }
                    SetAlgorithmRates(message.data);
                    return null;
                }

                case "balance":
                    SetBalance(message.value.Value);
                    return null;
                case "burn":
                    ApplicationStateManager.Burn(message.message.Value);
                    return null;
                case "exchange_rates":
                    SetExchangeRates(message.data.Value);
                    return null;
                case "essentials":
                    var ess = JsonConvert.DeserializeObject<EssentialsCall>(data);
                    ProcessEssentials(ess);
                    return null;
                case "mining.set.username":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    var btc = (string)message.username;
                    return miningSetUsername(btc);
                case "mining.set.worker":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    var worker = (string)message.worker;
                    return miningSetWorker(worker);
                case "mining.set.group":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    var group = (string) message.group;
                    return miningSetGroup(group);
                case "mining.enable":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    SetDevicesEnabled((string) message.device, true);
                    return null;
                case "mining.disable":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    SetDevicesEnabled((string) message.device, false);
                    return null;
                case "mining.start":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    StartMining((string) message.device);
                    return null;
                case "mining.stop":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    StopMining((string) message.device);
                    return null;
                case "mining.set.power_mode":
                    executed = true;
                    throwIfWeCannotHanldeRPC();
                    SetPowerMode((string) message.device, (PowerLevel) message.power_mode);
                    return null;
            }
            
            throw new RpcException("Operation not supported", ErrorCode.UnableToHandleRpc);
        }

        private static bool isRpcMethod(string method) {
            // well pretty much all RPCs start with mining.*
            switch (method) {
                case "mining.set.username":
                case "mining.set.worker":
                case "mining.set.group":
                case "mining.enable":
                case "mining.disable":
                case "mining.start":
                case "mining.stop":
                case "mining.set.power_mode":
                    return true;
            }
            return false;
        }

        private static void throwIfWeCannotHanldeRPC() {
            if (ApplicationStateManager.CalcRigStatus() == RigStatus.Pending) {
                throw new RpcException("Cannot handle RPC call Rig is in PENDING state", ErrorCode.UnableToHandleRpc);
            }
            if (ApplicationStateManager.IsInBenchmarkForm()) {
                throw new RpcException("Cannot handle RPC call Rig is in benchmarks form", ErrorCode.UnableToHandleRpc);
            }
            if (ApplicationStateManager.IsInSettingsForm()) {
                throw new RpcException("Cannot handle RPC call rig is in settings form", ErrorCode.UnableToHandleRpc);
            }
        }


        private static void SocketOnOnConnectionEstablished(object sender, EventArgs e)
        {
            // Send device to populate rig stats, and send device names
            SendMinerStatus(true);
        }

#endregion

#region Incoming socket calls

        private static void ProcessEssentials(EssentialsCall ess)
        {
            if (ess?.Versions?.Count > 1 && ess.Versions[1].Count == 2)
            {
                SetVersion(ess.Versions[1][0], ess.Versions[1][1]);
            }


            // this isn't really used anymore
            //if (ess?.Devices != null)
            //{
            //    foreach (var map in ess.Devices)
            //    {
            //        // Hacky way temporary

            //        if (!(map is JArray m && m.Count > 1)) continue;
            //        var name = m.Last().Value<string>();
            //        var i = m.First().Value<int>();

            //        foreach (var dev in ComputeDeviceManager.Available.Devices)
            //        {
            //            if (dev.Name.Contains(name))
            //                dev.TypeID = i;
            //        }
            //    }
            //}
        }

        private static void SetAlgorithmRates(JArray data)
        {
            try
            {
                var payingDict = new Dictionary<AlgorithmType, double>();
                if (data != null)
                {
                    foreach (var algo in data)
                    {
                        var algoKey = (AlgorithmType) algo[0].Value<int>();
                        payingDict[algoKey] = algo[1].Value<double>();
                    }
                }

                NHSmaData.UpdateSmaPaying(payingDict);
                
                OnSmaUpdate?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        private static void SetStableAlgorithms(JArray stable)
        {
            var stables = stable.Select(algo => (AlgorithmType) algo.Value<int>());
            NHSmaData.UpdateStableAlgorithms(stables);
        }

        private static void SetBalance(string balance)
        {
            try
            {
                if (double.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var bal))
                {
                    ApplicationStateManager.OnBalanceUpdate(bal);
                }
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        private static void SetVersion(string version, string link)
        {
            Version = version;
            VersionLink = link;
            ApplicationStateManager.OnVersionUpdate(version);
        }

        private static void SetExchangeRates(string data)
        {
            try
            {
                var exchange = JsonConvert.DeserializeObject<ExchangeRateJson>(data);
                if (exchange?.exchanges_fiat == null || exchange.exchanges == null) return;
                foreach (var exchangePair in exchange.exchanges)
                {
                    if (!exchangePair.TryGetValue("coin", out var coin) || coin != "BTC" ||
                        !exchangePair.TryGetValue("USD", out var usd) || 
                        !double.TryParse(usd, NumberStyles.Float, CultureInfo.InvariantCulture, out var usdD))
                        continue;

                    ExchangeRateApi.UsdBtcRate = usdD;
                    break;
                }

                ExchangeRateApi.UpdateExchangesFiat(exchange.exchanges_fiat);

                OnExchangeUpdate?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

#region Credentials setters (btc/username, worker, group)
        private static ExecutedInfo miningSetUsername(string btc)
        {
            var userSetResult = ApplicationStateManager.SetBTCIfValidOrDifferent(btc, true);
            switch (userSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    throw new RpcException("Bitcoin address invalid", ErrorCode.InvalidUsername);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    break;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change btc \"{btc}\" already set", ErrorCode.RedundantRpc);
            }
            return new ExecutedInfo { NewBtc = btc };
        }

        private static ExecutedInfo miningSetWorker(string worker)
        {
            var workerSetResult = ApplicationStateManager.SetWorkerIfValidOrDifferent(worker, true);
            switch (workerSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    throw new RpcException("Worker name invalid", ErrorCode.InvalidWorker);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    break;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change worker name \"{worker}\" already set", ErrorCode.RedundantRpc);
            }
            return new ExecutedInfo { NewWorker = worker };
        }

        private static ExecutedInfo miningSetGroup(string group)
        {
            var groupSetResult = ApplicationStateManager.SetGroupIfValidOrDifferent(group, true);
            switch (groupSetResult)
            {
                case ApplicationStateManager.SetResult.INVALID:
                    // TODO error code not correct
                    throw new RpcException("Group name invalid", ErrorCode.UnableToHandleRpc);
                case ApplicationStateManager.SetResult.CHANGED:
                    // we return executed
                    break;
                case ApplicationStateManager.SetResult.NOTHING_TO_CHANGE:
                    throw new RpcException($"Nothing to change group \"{group}\" already set", ErrorCode.RedundantRpc);
            }
            return new ExecutedInfo { NewRig = group };
        }
#endregion Credentials setters (btc/username, worker, group)

        private static bool SetDevicesEnabled(string devs, bool enabled)
        {
            bool allDevices = devs == "*";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = ComputeDeviceManager.Available.GetDeviceWithUuidOrB64Uuid(devs);

            // Check if RPC should execute
            // check if redundant rpc
            if (allDevices && enabled && ApplicationStateManager.IsEnableAllDevicesRedundantOperation()) {
                throw new RpcException("All devices are already enabled.", ErrorCode.RedundantRpc);
            }
            // all disable
            if (allDevices && !enabled && ApplicationStateManager.IsDisableAllDevicesRedundantOperation()) {
                throw new RpcException("All devices are already disabled.", ErrorCode.RedundantRpc);
            }
            // if single and doesn't exist
            if (!allDevices && deviceWithUUID == null) {
                throw new RpcException("Device not found", ErrorCode.NonExistentDevice);
            }
            // if we have the device but it is redundant
            if (!allDevices && deviceWithUUID.IsDisabled == !enabled) {
                var stateStr = enabled ? "enabled" : "disabled";
                throw new RpcException($"Devices with uuid {devs} is already {stateStr}.", ErrorCode.RedundantRpc);
            }

            // if got here than we can execute the call
            ApplicationStateManager.SetDeviceEnabledState(null, (devs, enabled));
            // TODO invoke the event for controls that use it
            OnDeviceUpdate?.Invoke(null, new DeviceUpdateEventArgs(ComputeDeviceManager.Available.Devices));
            // TODO this used to return 'anyStillRunning' but we are actually checking if there are any still enabled left
            var anyStillEnabled = ComputeDeviceManager.Available.Devices.Any();
            return anyStillEnabled;
        }

#region Start
        private static void startMiningAllDevices() {
            var allDisabled = ComputeDeviceManager.Available.Devices.All(dev => dev.IsDisabled);
            if (allDisabled) {
                throw new RpcException("All devices are disabled cannot start", ErrorCode.DisabledDevice);
            }
            var (success, msg) = ApplicationStateManager.StartAllAvailableDevices(true);
            if (!success) {
                throw new RpcException(msg, ErrorCode.RedundantRpc);
            }
        }

        private static void startMiningOnDeviceWithUuid(string uuid) {
            string errMsgForUuid = $"Cannot start device with uuid {uuid}";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = ComputeDeviceManager.Available.GetDeviceWithUuidOrB64Uuid(uuid);
            if (deviceWithUUID == null) {
                throw new RpcException($"{errMsgForUuid}. Device not found.", ErrorCode.NonExistentDevice);
            }
            if (deviceWithUUID.IsDisabled) {
                throw new RpcException($"{errMsgForUuid}. Device is disabled.", ErrorCode.DisabledDevice);
            }
            var (success, msg) = ApplicationStateManager.StartDevice(deviceWithUUID);
            if (!success) {
                // TODO this can also be an error
                throw new RpcException($"{errMsgForUuid}. {msg}.", ErrorCode.RedundantRpc);
            }
        }

        private static void StartMining(string devs)
        {
            bool allDevices = devs == "*";
            if (allDevices) {
                startMiningAllDevices();
            } else {
                startMiningOnDeviceWithUuid(devs);
            }
        }
#endregion Start

#region Stop
        private static void stopMiningAllDevices()
        {
            var allDisabled = ComputeDeviceManager.Available.Devices.All(dev => dev.IsDisabled);
            if (allDisabled) {
                throw new RpcException("All devices are disabled cannot stop", ErrorCode.DisabledDevice);
            }
            var (success, msg) = ApplicationStateManager.StopAllDevice();
            if (!success) {
                throw new RpcException(msg, ErrorCode.RedundantRpc);
            }
        }

        private static void stopMiningOnDeviceWithUuid(string uuid)
        {
            string errMsgForUuid = $"Cannot stop device with uuid {uuid}";
            // get device with uuid if it exists, devs can be single device uuid
            var deviceWithUUID = ComputeDeviceManager.Available.GetDeviceWithUuidOrB64Uuid(uuid);
            if (deviceWithUUID == null) {
                throw new RpcException($"{errMsgForUuid}. Device not found.", ErrorCode.NonExistentDevice);
            }
            if (deviceWithUUID.IsDisabled) {
                throw new RpcException($"{errMsgForUuid}. Device is disabled.", ErrorCode.DisabledDevice);
            }
            var (success, msg) = ApplicationStateManager.StopDevice(deviceWithUUID);
            if (!success) {
                // TODO this can also be an error
                throw new RpcException($"{errMsgForUuid}. {msg}.", ErrorCode.RedundantRpc);
            }
        }

        private static void StopMining(string devs)
        {
            bool allDevices = devs == "*";
            if (allDevices) {
                stopMiningAllDevices();
            } else {
                stopMiningOnDeviceWithUuid(devs);
            }
        }
#endregion Stop

        private static void SetPowerMode(string device, PowerLevel level)
        {
            var devs = device == "*" ? 
                ComputeDeviceManager.Available.Devices : 
                ComputeDeviceManager.Available.Devices.Where(d => d.B64Uuid == device);

            var found = false;

            foreach (var dev in devs)
            {
                if (!(dev is CudaComputeDevice cuda)) continue;
                cuda.SetPowerTarget(level);
                found = true;
            }

            if (!found)
            {
                throw new RpcException("No devices settable devices found", ErrorCode.UnableToHandleRpc);
            }
        }

#endregion

#region Outgoing socket calls

        public static void SetCredentials(string btc, string worker, string group)
        {
            if (BitcoinAddress.ValidateBitcoinAddress(btc) && BitcoinAddress.ValidateWorkerName(worker))
            {
                // Send as task since SetCredentials is called from UI threads
                Task.Factory.StartNew(() =>
                {
                    SendMinerStatus(false);
                    _socket?.StartConnection(btc, worker, group);
                });
            }
        }

        private static void SendMinerStatus(bool sendDeviceNames)
        {
            var devices = ComputeDeviceManager.Available.Devices;
            var rigStatus = ApplicationStateManager.CalcRigStatusString();
            var paramList = new List<JToken>
            {
                rigStatus
            };

            var deviceList = new JArray();
            foreach (var device in devices)
            {
                try
                {
                    var array = new JArray
                    {
                        sendDeviceNames ? device.Name : "",
                        device.B64Uuid  // TODO
                    };
                    var status = DeviceReportStatus(device.DeviceType, device.State);
                    array.Add(status);

                    array.Add((int)Math.Round(device.Load));

                    // TODO algo speeds
                    array.Add(new JArray());

                    // Hardware monitoring
                    array.Add((int) Math.Round(device.Temp));
                    array.Add(device.FanSpeed);
                    array.Add((int) Math.Round(device.PowerUsage));

                    // Power mode
                    if (device is CudaComputeDevice cuda)
                    {
                        array.Add((int) cuda.PowerLevel);
                    }
                    else
                    {
                        array.Add(0);
                    }

                    // Intensity mode
                    array.Add(0);

                    deviceList.Add(array);
                }
                catch (Exception e) { Helpers.ConsolePrint("SOCKET", e.ToString()); }
            }

            paramList.Add(deviceList);

            var data = new NicehashDeviceStatus
            {
                param = paramList
            };
            var sendData = JsonConvert.SerializeObject(data);

            // This function is run every minute and sends data every run which has two auxiliary effects
            // Keeps connection alive and attempts reconnection if internet was dropped
            _socket?.SendData(sendData);
        }

        private static void MinerStatus_Tick(object state)
        {
            Helpers.ConsolePrint("SOCKET", "SendMinerStatus Tick 'miner.status'");
            SendMinerStatus(false);
        }

        private static void SendExecuted(ExecutedInfo info, int? id, int code = 0, string message = null)
        {
            // First set status
            SendMinerStatus(false);
            // Then executed
            var data = new ExecutedCall(id ?? -1, code, message).Serialize();
            _socket?.SendData(data);
            // Login if we have to
            if (info?.LoginNeeded ?? false)
            {
                _socket?.StartConnection(info.NewBtc, info.NewWorker, info.NewRig);
            }
        }

#endregion

        public static void StateChanged()
        {
            SendMinerStatus(false);
        }

        public static string GetNiceHashApiData(string url, string worker)
        {
            var responseFromServer = "";
            try
            {
                var wr = (HttpWebRequest) WebRequest.Create(url);
                wr.UserAgent = "NiceHashMiner/" + Application.ProductVersion;
                if (worker.Length > 64) worker = worker.Substring(0, 64);
                wr.Headers.Add("NiceHash-Worker-ID", worker);
                wr.Timeout = 30 * 1000;
                var response = wr.GetResponse();
                var ss = response.GetResponseStream();
                if (ss != null)
                {
                    ss.ReadTimeout = 20 * 1000;
                    var reader = new StreamReader(ss);
                    responseFromServer = reader.ReadToEnd();
                    if (responseFromServer.Length == 0 || responseFromServer[0] != '{')
                        throw new Exception("Not JSON!");
                    reader.Close();
                }
                response.Close();
            }
            catch (Exception ex)
            {
                Helpers.ConsolePrint("NICEHASH", ex.Message);
                return null;
            }

            return responseFromServer;
        }
    }
}
#endif