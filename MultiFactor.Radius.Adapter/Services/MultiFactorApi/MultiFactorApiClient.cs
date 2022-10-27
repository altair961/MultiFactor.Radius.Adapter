﻿//Copyright(c) 2020 MultiFactor
//Please see licence at 
//https://github.com/MultifactorLab/MultiFactor.Radius.Adapter/blob/master/LICENSE.md


using MultiFactor.Radius.Adapter.Configuration;
using MultiFactor.Radius.Adapter.Core;
using MultiFactor.Radius.Adapter.Server;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MultiFactor.Radius.Adapter.Services.MultiFactorApi
{
    /// <summary>
    /// Service to interact with multifactor web api
    /// </summary>
    public class MultiFactorApiClient
    {
        private ServiceConfiguration _serviceConfiguration;
        private ILogger _logger;

        private static readonly ConcurrentDictionary<string, AuthenticatedClient> _authenticatedClients = new ConcurrentDictionary<string, AuthenticatedClient>();

        public MultiFactorApiClient(ServiceConfiguration serviceConfiguration, ILogger logger)
        {
            _serviceConfiguration = serviceConfiguration ?? throw new ArgumentNullException(nameof(serviceConfiguration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PacketCode> CreateSecondFactorRequest(PendingRequest request, ClientConfiguration clientConfig)
        {
            var remoteHost = request.RequestPacket.RemoteHostName;
            var userName = request.UserName;
            var displayName = request.DisplayName;
            var email = request.EmailAddress;
            var userPhone = request.UserPhone;
            var callingStationId = request.RequestPacket.CallingStationId;

            string calledStationId = null;

            if (request.RequestPacket.IsWinLogon) //only for winlogon yet
            {
                calledStationId = request.RequestPacket.CalledStationId;
            }

            if (clientConfig.UseUpnAsIdentity)
            {
                if (string.IsNullOrEmpty(request.Upn))
                {
                    throw new ArgumentNullException("UserPrincipalName");
                }

                userName = request.Upn;
            }

            //remove user information for privacy
            switch (clientConfig.PrivacyMode)
            {
                case PrivacyMode.Full:
                    displayName = null;
                    email = null;
                    userPhone = null;
                    callingStationId = "";
                    calledStationId = null;
                    break;
            }

            //try to get authenticated client to bypass second factor if configured
            if (clientConfig.BypassSecondFactorPeriod > 0)
            {
                if (TryHitCache(remoteHost, userName, clientConfig))
                {
                    _logger.Information("Bypass second factor for user '{user:l}' from {host:l}:{port}", userName, request.RemoteEndpoint.Address, request.RemoteEndpoint.Port);
                    return PacketCode.AccessAccept;
                }
            }

            var url = _serviceConfiguration.ApiUrl + "/access/requests/ra";
            var payload = new
            {
                Identity = userName,
                Name = displayName,
                Email = email,
                Phone = userPhone,
                PassCode = GetPassCodeOrNull(request, clientConfig),
                CallingStationId = callingStationId,
                CalledStationId = calledStationId,
                Capabilities = new
                {
                    InlineEnroll = true
                },
                GroupPolicyPreset = new
                {
                    clientConfig.SignUpGroups
                }
            };

            try
            {
                var response = await SendRequest(url, payload, clientConfig);
                var responseCode = ConvertToRadiusCode(response);

                request.State = response?.Id;
                request.ReplyMessage = response?.ReplyMessage;

                if (responseCode == PacketCode.AccessAccept && !response.Bypassed)
                {
                    LogGrantedInfo(userName, response, request);

                    if (clientConfig.BypassSecondFactorPeriod > 0)
                    {
                        SetCache(remoteHost, userName);
                    }
                }

                if (responseCode == PacketCode.AccessReject)
                {
                    _logger.Warning("Second factor verification for user '{user:l}' from {host:l}:{port} failed with reason='{reason:l}'. User phone {phone:l}",
                        userName, request.RemoteEndpoint.Address, request.RemoteEndpoint.Port, response?.ReplyMessage, response?.Phone);         
                }

                return responseCode;
            }
            catch (Exception ex)
            {
                return HandleException(ex, userName, request, clientConfig);
            }
        }

        public async Task<PacketCode> Challenge(PendingRequest request, ClientConfiguration clientConfig, string userName, string answer, string state)
        {
            var url = _serviceConfiguration.ApiUrl + "/access/requests/ra/challenge";
            var payload = new
            {
                Identity = userName,
                Challenge = answer,
                RequestId = state
            };

            try
            {
                var response = await SendRequest(url, payload, clientConfig);
                var responseCode = ConvertToRadiusCode(response);

                request.ReplyMessage = response.ReplyMessage;

                if (responseCode == PacketCode.AccessAccept && !response.Bypassed)
                {
                    LogGrantedInfo(userName, response, request);
                }

                return responseCode;
            }
            catch (Exception ex)
            {
                return HandleException(ex, userName, request, clientConfig);
            }

        }

        private async Task<MultiFactorAccessRequest> SendRequest(string url, object payload, ClientConfiguration clientConfiguration)
        {
            try
            {
                //make sure we can communicate securely
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 100;

                var json = JsonConvert.SerializeObject(payload);

                _logger.Debug("Sending request to API: {@payload}", payload);

                var requestData = Encoding.UTF8.GetBytes(json);
                byte[] responseData = null;

                //basic authorization
                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes(clientConfiguration.MultifactorApiKey + ":" + clientConfiguration.MultiFactorApiSecret));

                using (var web = new WebClient())
                {
                    web.Headers.Add("Content-Type", "application/json");
                    web.Headers.Add("Authorization", "Basic " + auth);

                    if (!string.IsNullOrEmpty(_serviceConfiguration.ApiProxy))
                    {
                        _logger.Debug("Using proxy " + _serviceConfiguration.ApiProxy);
                        var proxyUri = new Uri(_serviceConfiguration.ApiProxy);
                        web.Proxy = new WebProxy(proxyUri);

                        if (!string.IsNullOrEmpty(proxyUri.UserInfo))
                        {
                            var credentials = proxyUri.UserInfo.Split(new[] { ':' }, 2);
                            web.Proxy.Credentials = new NetworkCredential(credentials[0], credentials[1]);
                        }
                    }

                    responseData = await web.UploadDataTaskAsync(url, "POST", requestData);
                }

                json = Encoding.UTF8.GetString(responseData);
                var response = JsonConvert.DeserializeObject<MultiFactorApiResponse<MultiFactorAccessRequest>>(json);

                _logger.Debug("Received response from API: {@response}", response);

                if (!response.Success)
                {
                    _logger.Warning("Got unsuccessful response from API: {@response}", response);
                }

                return response.Model;
            }
            catch (Exception ex)
            {
                throw new MultifactorApiUnreachableException($"Multifactor API host unreachable: {url}. Reason: {ex.Message}", ex);
            }
        }

        private PacketCode HandleException(Exception ex, string username, PendingRequest request, ClientConfiguration clientConfig)
        {
            if (ex is MultifactorApiUnreachableException apiEx)
            {
                _logger.Error("Error occured while requesting API for user '{user:l}' from {host:l}:{port}, {msg:l}",
                    username,
                    request.RemoteEndpoint.Address,
                    request.RemoteEndpoint.Port,
                    apiEx.Message);

                if (clientConfig.BypassSecondFactorWhenApiUnreachable)
                {
                    _logger.Warning("Bypass second factor for user '{user:l}' from {host:l}:{port}",
                        username,
                        request.RemoteEndpoint.Address,
                        request.RemoteEndpoint.Port);
                    return ConvertToRadiusCode(MultiFactorAccessRequest.Bypass);
                }
            }

            return ConvertToRadiusCode(null);
        }

        private PacketCode ConvertToRadiusCode(MultiFactorAccessRequest multifactorAccessRequest)
        {
            if (multifactorAccessRequest == null)
            {
                return PacketCode.AccessReject;
            }

            switch (multifactorAccessRequest.Status)
            {
                case Literals.RadiusCode.Granted:     //authenticated by push
                    return PacketCode.AccessAccept;
                case Literals.RadiusCode.Denied:
                    return PacketCode.AccessReject; //access denied
                case Literals.RadiusCode.AwaitingAuthentication:
                    return PacketCode.AccessChallenge;  //otp code required
                default:
                    _logger.Warning($"Got unexpected status from API: {multifactorAccessRequest.Status}");
                    return PacketCode.AccessReject; //access denied
            }
        }

        private bool TryHitCache(string remoteHost, string userName, ClientConfiguration clientConfiguration)
        {
            if (string.IsNullOrEmpty(remoteHost))
            {
                _logger.Warning($"Remote host parameter miss for user {userName}");
                return false;
            }

            var id = AuthenticatedClient.CreateId(remoteHost, userName);
            if (_authenticatedClients.TryGetValue(id, out var authenticatedClient))
            {
                _logger.Debug($"User {userName} from {remoteHost} authenticated {authenticatedClient.Elapsed.ToString("hh\\:mm\\:ss")} ago. Bypass period: {clientConfiguration.BypassSecondFactorPeriod}m");

                if (authenticatedClient.Elapsed.TotalMinutes <= (clientConfiguration.BypassSecondFactorPeriod ?? 0))
                {
                    return true;
                }
                else
                {
                    _authenticatedClients.TryRemove(id, out _);
                }
            }

            return false;
        }

        private void SetCache(string remoteHost, string userName)
        {
            if (string.IsNullOrEmpty(remoteHost))
            {
                return;
            }

            var client = new AuthenticatedClient
            {
                RemoteHost = remoteHost,
                UserName = userName,
                AuthenticatedAt = DateTime.Now
            };

            if (!_authenticatedClients.ContainsKey(client.Id))
            {
                _authenticatedClients.TryAdd(client.Id, client);
            }
        }

        private string GetPassCodeOrNull(PendingRequest request, ClientConfiguration clientConfiguration)
        {
            //check static challenge
            var challenge = request.RequestPacket.TryGetChallenge();
            if (challenge != null)
            {
                return challenge;
            }

            //check password challenge (otp or passcode)
            var userPassword = request.RequestPacket.TryGetUserPassword();

            //only if first authentication factor is None, assuming that Password contains OTP code
            if (clientConfiguration.FirstFactorAuthenticationSource != AuthenticationSource.None)
            {
                return null;
            }

            /* valid passcodes:
             *  6 digits: otp
             *  t: Telegram
             *  m: MobileApp
             *  s: SMS
             *  c: PhoneCall
             */

            if (string.IsNullOrEmpty(userPassword))
            {
                return null;
            }

            var isOtp = Regex.IsMatch(userPassword.Trim(), "^[0-9]{1,6}$");
            if (isOtp)
            {
                return userPassword.Trim();
            }

            if (new[] { "t", "m", "s", "c" }.Any(c => c == userPassword.Trim().ToLower()))
            {
                return userPassword.Trim().ToLower();
            }

            //not a passcode
            return null;
        }

        private void LogGrantedInfo(string userName, MultiFactorAccessRequest response, PendingRequest request)
        {
            string countryValue = null;
            string regionValue = null;
            string cityValue = null;
            string callingStationId = request?.RequestPacket?.CallingStationId;

            if (response != null && IPAddress.TryParse(callingStationId, out var ip))
            {
                countryValue = response.CountryCode;
                regionValue = response.Region;
                cityValue = response.City;
                callingStationId = ip.ToString();
            }

            _logger.Information("Second factor for user '{user:l}' verified successfully. Authenticator: '{authenticator:l}', account: '{account:l}', country: '{country:l}', region: '{region:l}', city: '{city:l}', calling-station-id: {clientIp}",
                        userName,
                        response?.Authenticator,
                        response?.Account,
                        countryValue,
                        regionValue,
                        cityValue,
                        callingStationId);
        }
    }
}
