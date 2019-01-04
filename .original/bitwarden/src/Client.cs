// Copyright (C) 2018 Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bitwarden
{
    internal static class Client
    {
        public static Account[] OpenVault(string username, string password, string deviceId, Ui ui, IHttpClient http)
        {
            var jsonHttp = new JsonHttpClient(http, BaseUrl);

            // 1. Request the number of KDF iterations needed to derive the key
            var iterations = RequestKdfIterationCount(username, jsonHttp);

            // 2. Derive the master encryption key or KEK (key encryption key)
            var key = Crypto.DeriveKey(username, password, iterations);

            // 3. Hash the password that is going to be sent to the server
            var hash = Crypto.HashPassword(password, key);

            // 4. Authenticate with the server and get the token
            var token = Login(username, hash, deviceId, ui, jsonHttp);

            // 5. All subsequent requests are signed with this header
            var authJsonHttp = new JsonHttpClient(http,
                                                  BaseUrl,
                                                  new Dictionary<string, string> {{"Authorization", token}});

            // 6. Fetch the vault
            var encryptedVault = DownloadVault(authJsonHttp);

            return DecryptVault(encryptedVault, key);
        }

        //
        // Internal
        //

        internal static int RequestKdfIterationCount(string username, JsonHttpClient jsonHttp)
        {
            var info = RequestKdfInfo(username, jsonHttp);
            if (info.Kdf != Response.KdfMethod.Pbkdf2Sha256)
                throw new ClientException(ClientException.FailureReason.UnsupportedFeature,
                                          $"KDF method {info.Kdf} is not supported");

            return info.KdfIterations;
        }

        internal static Response.KdfInfo RequestKdfInfo(string username, JsonHttpClient jsonHttp)
        {
            try
            {
                return jsonHttp.Post<Response.KdfInfo>("api/accounts/prelogin",
                                                       new Dictionary<string, string> { { "email", username } });
            }
            catch (ClientException e)
            {
                // The web client seems to ignore network errors. Default to 5000 iterations.
                if (IsHttp400To500(e))
                    return DefaultKdfInfo;

                throw MakeSpecializedError(e);
            }
        }

        internal static string Login(string username,
                                     byte[] passwordHash,
                                     string deviceId,
                                     Ui ui,
                                     JsonHttpClient jsonHttp)
        {
            var response = RequestAuthToken(username, passwordHash, deviceId, jsonHttp);

            // Simple password login (no 2FA) succeeded
            if (response.AuthToken != null)
                return response.AuthToken;

            var secondFactor = response.SecondFactor;
            if (secondFactor.Methods == null || secondFactor.Methods.Count == 0)
                throw new ClientException(ClientException.FailureReason.InvalidResponse,
                                          "Expected a non empty list of available 2FA methods");

            var method = ChooseSecondFactorMethod(secondFactor);
            var extra = secondFactor.Methods[method];
            Ui.Passcode passcode;
            switch (method)
            {
            case Response.SecondFactorMethod.GoogleAuth:
                passcode = ui.ProvideGoogleAuthPasscode();
                break;
            case Response.SecondFactorMethod.Email:
                if (secondFactor.Methods.Count != 1)
                    throw new InvalidOperationException("Logical error: email 2FA method should be chosen " +
                                                        "only when there are no other options left");
                // When only email 2FA present, the email is sent by the server right away
                // and we don't need to trigger it. Otherwise we don't support it at the moment.
                passcode = ui.ProvideEmailPasscode((string)extra["Email"] ?? "");
                break;
            case Response.SecondFactorMethod.Duo:
                passcode = Duo.Authenticate((string)extra["Host"] ?? "",
                                            (string)extra["Signature"] ?? "",
                                            ui,
                                            jsonHttp.Http);
                break;
            case Response.SecondFactorMethod.YubiKey:
                passcode = ui.ProvideYubiKeyPasscode();
                break;
            default:
                throw new ClientException(ClientException.FailureReason.UnsupportedFeature,
                                          $"2FA method {method} is not supported");
            }

            if (passcode == null)
                throw new ClientException(ClientException.FailureReason.UserCanceledSecondFactor,
                                          "Second factor step is canceled by the user");

            var secondFactorResponse = RequestAuthToken(username,
                                                        passwordHash,
                                                        deviceId,
                                                        new SecondFactorOptions(method,
                                                                                passcode.Code,
                                                                                passcode.RememberMe),
                                                        jsonHttp);
            if (secondFactorResponse.AuthToken != null)
                return secondFactorResponse.AuthToken;

            throw new ClientException(ClientException.FailureReason.IncorrectSecondFactorCode,
                                      "Second factor code is not correct");
        }

        internal static Response.SecondFactorMethod ChooseSecondFactorMethod(Response.SecondFactor secondFactor)
        {
            var methods = secondFactor.Methods;
            if (methods == null || methods.Count == 0)
                throw new InvalidOperationException("Logical error: should be called with non empty list of methods");

            if (methods.Count == 1)
                return methods.ElementAt(0).Key;

            foreach (var i in SecondFactorMethodPreferenceOrder)
                if (methods.ContainsKey(i))
                    return i;

            return methods.ElementAt(0).Key;
        }

        internal struct TokenOrSecondFactor
        {
            public readonly string AuthToken;
            public readonly Response.SecondFactor SecondFactor;

            public TokenOrSecondFactor(string authToken)
            {
                AuthToken = authToken;
                SecondFactor = new Response.SecondFactor();
            }

            public TokenOrSecondFactor(Response.SecondFactor secondFactor)
            {
                AuthToken = null;
                SecondFactor = secondFactor;
            }
        }

        internal class SecondFactorOptions
        {
            public readonly Response.SecondFactorMethod Method;
            public readonly string Passcode;
            public readonly bool RememberMe;

            public SecondFactorOptions(Response.SecondFactorMethod method, string passcode, bool rememberMe)
            {
                Method = method;
                Passcode = passcode;
                RememberMe = rememberMe;
            }
        }

        internal static TokenOrSecondFactor RequestAuthToken(string username,
                                                             byte[] passwordHash,
                                                             string deviceId,
                                                             JsonHttpClient jsonHttp)
        {
            return RequestAuthToken(username, passwordHash, deviceId, null, jsonHttp);
        }

        // secondFactorOptions is optional
        internal static TokenOrSecondFactor RequestAuthToken(string username,
                                                             byte[] passwordHash,
                                                             string deviceId,
                                                             SecondFactorOptions secondFactorOptions,
                                                             JsonHttpClient jsonHttp)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    {"username", username},
                    {"password", passwordHash.ToBase64()},
                    {"grant_type", "password"},
                    {"scope", "api offline_access"},
                    {"client_id", "web"},
                    {"deviceType", "9"},
                    {"deviceName", "chrome"},
                    {"deviceIdentifier", deviceId},
                };

                if (secondFactorOptions != null)
                {
                    parameters["twoFactorProvider"] = secondFactorOptions.Method.ToString("d");
                    parameters["twoFactorToken"] = secondFactorOptions.Passcode;
                    parameters["twoFactorRemember"] = secondFactorOptions.RememberMe ? "1" : "0";
                }

                var response = jsonHttp.PostForm<Response.AuthToken>("identity/connect/token", parameters);
                return new TokenOrSecondFactor($"{response.TokenType} {response.AccessToken}");
            }
            catch (ClientException e)
            {
                // .NET WebClinet throws exceptions on HTTP errors. In the case of 2FA the server
                // returns some 400+ HTTP error and the response contains extra information about
                // the available 2FA methods. JsonHttpClient doesn't handle parsing of the response
                // on error. So we have to fish it out of the original exception and the attached
                // HTTP response object.
                // TODO: Write a test for this situation. It's not very easy at the moment, since
                //       we have to throw some pretty complex made up exceptions.
                var secondFactor = ExtractSecondFactorFromResponse(e);
                if (secondFactor.HasValue)
                    return new TokenOrSecondFactor(secondFactor.Value);

                throw MakeSpecializedError(e);
            }
        }

        internal static Response.SecondFactor? ExtractSecondFactorFromResponse(ClientException e)
        {
            if (!IsHttp400To500(e))
                return null;

            var response = GetHttpResponse(e);
            if (response == null)
                return null;

            try
            {
                return JsonConvert.DeserializeObject<Response.SecondFactor>(response);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static Response.Vault DownloadVault(JsonHttpClient jsonHttp)
        {
            try
            {
                return jsonHttp.Get<Response.Vault>("api/sync?excludeDomains=true");
            }
            catch (ClientException e)
            {
                throw MakeSpecializedError(e);
            }
        }

        internal static Account[] DecryptVault(Response.Vault vault, byte[] key)
        {
            // By default use the derived key, this is true for some old vaults.
            var vaultKey = key;

            // The newer vaults have a key stored in the profile section. It's encrypted
            // with the derived key, with is effectively a KEK now.
            var encryptedVaultKey = vault.Profile.Key;
            if (encryptedVaultKey != null)
                vaultKey = DecryptToBytes(vault.Profile.Key, key);

            var folders = ParseFolders(vault.Folders, vaultKey);

            return vault.Ciphers
                .Where(i => i.Type == Response.ItemType.Login)
                .Select(i => ParseAccountItem(i, vaultKey, folders)).ToArray();
        }

        internal static Dictionary<string, string> ParseFolders(Response.Folder[] folders, byte[] key)
        {
            return folders.ToDictionary(i => i.Id, i => DecryptToString(i.Name, key));
        }

        internal static Account ParseAccountItem(Response.Item item, byte[] key, Dictionary<string, string> folders)
        {
            var folder = item.FolderId != null && folders.ContainsKey(item.FolderId)
                ? folders[item.FolderId]
                : "";

            return new Account(id: item.Id,
                               name: DecryptToStringOrBlank(item.Name, key),
                               username: DecryptToStringOrBlank(item.Login.Username, key),
                               password: DecryptToStringOrBlank(item.Login.Password, key),
                               url: DecryptToStringOrBlank(item.Login.Uri, key),
                               note: DecryptToStringOrBlank(item.Notes, key),
                               folder: folder);
        }

        internal static byte[] DecryptToBytes(string s, byte[] key)
        {
            return CipherString.Parse(s).Decrypt(key);
        }

        internal static string DecryptToString(string s, byte[] key)
        {
            return  DecryptToBytes(s, key).ToUtf8();
        }

        // s may be null
        internal static string DecryptToStringOrBlank(string s, byte[] key)
        {
            return s == null ? "" : DecryptToString(s, key);
        }

        internal static HttpStatusCode? GetHttpStatus(ClientException e)
        {
            if (e.Reason != ClientException.FailureReason.NetworkError)
                return null;

            var we = e.InnerException as WebException;
            if (we == null || we.Status != WebExceptionStatus.ProtocolError)
                return null;

            var wr = we.Response as HttpWebResponse;
            if (wr == null)
                return null;

            return wr.StatusCode;
        }

        internal static bool IsHttp400To500(ClientException e)
        {
            var status = GetHttpStatus(e);
            return status != null && (int)status.Value / 100 == 4;
        }

        internal static string GetHttpResponse(ClientException e)
        {
            if (e.Reason != ClientException.FailureReason.NetworkError)
                return null;

            var we = e.InnerException as WebException;
            if (we == null || we.Status != WebExceptionStatus.ProtocolError)
                return null;

            var wr = we.Response as HttpWebResponse;
            if (wr == null)
                return null;

            var stream = wr.GetResponseStream();
            if (stream == null)
                return null;

            // Leave the response stream open to be able to read it again later
            using (var r = new StreamReader(stream,
                                            Encoding.UTF8,
                                            detectEncodingFromByteOrderMarks: true,
                                            bufferSize: 1024,
                                            leaveOpen: true))
            {
                var response = r.ReadToEnd();

                // Rewind it back not to make someone very confused when they try to read from it
                if (stream.CanSeek)
                    stream.Seek(0, SeekOrigin.Begin);

                return response;
            }
        }

        internal static string GetServerErrorMessage(ClientException e)
        {
            var response = GetHttpResponse(e);
            if (response == null)
                return null;

            try
            {
                var parsed = JObject.Parse(response);
                return (string)(parsed["ErrorModel"] ?? parsed)["Message"];
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static ClientException MakeSpecializedError(ClientException e)
        {
            if (!IsHttp400To500(e))
                return e;

            var message = GetServerErrorMessage(e);
            if (message == null)
                return e;

            if (message.Contains("Username or password is incorrect"))
                return new ClientException(ClientException.FailureReason.IncorrectCredentials, message, e);

            if (message.Contains("Two-step token is invalid"))
                return new ClientException(ClientException.FailureReason.IncorrectSecondFactorCode, message, e);

            return new ClientException(ClientException.FailureReason.RespondedWithError, message, e);
        }

        //
        // Private
        //

        private const string BaseUrl = "https://vault.bitwarden.com";

        private static readonly Response.SecondFactorMethod[] SecondFactorMethodPreferenceOrder =
        {
            Response.SecondFactorMethod.YubiKey,
            Response.SecondFactorMethod.Duo,
            Response.SecondFactorMethod.GoogleAuth,
            Response.SecondFactorMethod.Email // Must be the last one!
        };

        private static readonly Response.KdfInfo DefaultKdfInfo = new Response.KdfInfo
        {
            Kdf = Response.KdfMethod.Pbkdf2Sha256,
            KdfIterations = 5000
        };
    }
}
