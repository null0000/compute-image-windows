/*
 * Copyright 2015 Google Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Security.Permissions;
using System.Security.Principal;
using System.Text;
using Google.ComputeEngine.Common;

namespace Google.ComputeEngine.Agent
{
    /// <summary>
    /// The JSON object format for user credentials.
    /// </summary>
    [DataContract]
    internal sealed class GoogleCredentialsJson
    {
        [DataMember(Name = "passwordFound")]
        internal bool PasswordFound { get; set; }

        [DataMember(Name = "exponent")]
        internal string Exponent { get; set; }

        [DataMember(Name = "modulus")]
        internal string Modulus { get; set; }

        [DataMember(Name = "userName")]
        internal string UserName { get; set; }

        [DataMember(Name = "encryptedPassword", EmitDefaultValue = false)]
        internal string EncryptedPassword { get; set; }

        [DataMember(Name = "errorMessage", EmitDefaultValue = false)]
        internal string Error { get; set; }
    }

    public sealed class AccountsWriter : IAgentWriter<List<WindowsKey>>
    {
        public enum UserPrivEnum { UserPrivGuest, UserPrivUser, UserPrivAdmin }

        private const int UfScript = 0x0001;
        private const int UfNormalAccount = 0x0200;
        private const int UfDontExpirePasswd = 0x10000;
        private const string RegistryKeyPath = @"SOFTWARE\Google\ComputeEngine";
        private const string RegistryKey = "PublicKeys";
        private readonly RegistryWriter registryWriter = new RegistryWriter(RegistryKeyPath);

        private static void PrintCredentialsJson(
            bool passwordFound,
            string exponent,
            string modulus,
            string userName,
            string encryptedPassword = null,
            string error = null)
        {
            GoogleCredentialsJson credentialsJson = new GoogleCredentialsJson
            {
                PasswordFound = passwordFound,
                Exponent = exponent,
                Modulus = modulus,
                UserName = userName,
                EncryptedPassword = encryptedPassword,
                Error = error
            };

            string serializedCredentials = MetadataSerializer.SerializeMetadata<GoogleCredentialsJson>(credentialsJson);
            Logger.LogWithCom(EventLogEntryType.Information, "COM4", "{0}", serializedCredentials);
        }

        private static void PrintEncryptedCredentials(NetworkCredential credentials, string modulus, string exponent)
        {
            // The public key data is encoded in base64.
            if (string.IsNullOrEmpty(modulus) || string.IsNullOrEmpty(exponent))
            {
                Logger.Info("Invalid public key. Modulus: [{0}]. Exponent: [{0}]", modulus, exponent);
                return;
            }

            try
            {
                ASCIIEncoding byteConverter = new ASCIIEncoding();

                byte[] passwordData = byteConverter.GetBytes(credentials.Password);
                byte[] modulusData = Convert.FromBase64String(modulus);
                byte[] exponentData = Convert.FromBase64String(exponent);
                byte[] encryptedPassword;

                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
                {
                    RSAParameters rsaKeyInfo = rsa.ExportParameters(false);
                    rsaKeyInfo.Modulus = modulusData;
                    rsaKeyInfo.Exponent = exponentData;
                    rsa.ImportParameters(rsaKeyInfo);
                    if (rsa.KeySize != 2048)
                    {
                        Logger.Info("Expected a 2048 bit RSA key. Key size found: {0}.", rsa.KeySize);
                        return;
                    }
                    encryptedPassword = rsa.Encrypt(passwordData, true);
                    string base64password = Convert.ToBase64String(encryptedPassword);

                    PrintCredentialsJson(
                        passwordFound: true,
                        exponent: exponent,
                        modulus: modulus,
                        userName: credentials.UserName,
                        encryptedPassword: base64password);
                }
            }
            catch (CryptographicException e)
            {
                PrintCredentialsJson(
                    passwordFound: false,
                    exponent: exponent,
                    modulus: modulus,
                    userName: credentials.UserName,
                    error: e.Message);
            }
        }

        private static string GeneratePassword()
        {
            return System.Web.Security.Membership.GeneratePassword(15, 5);
        }

        private static void FailWithError(
            string method,
            NativeMethods.NetUserRetEnum netUserRet = NativeMethods.NetUserRetEnum.NerrSuccess)
        {
            int error;
            string message;

            if (NativeMethods.NetUserRetEnum.NerrSuccess != netUserRet)
            {
                error = (int)netUserRet;
                message = netUserRet.ToString();
            }
            else
            {
                error = Marshal.GetLastWin32Error();
                message = new Win32Exception(error).Message;
            }
            Logger.Warning("{0} failed with error {1}: {2}", method, error, message);
            throw new Win32Exception(error, message);
        }

        private static string GetAdminGroupName()
        {
            // Get the built in administrators account name.
            StringBuilder adminGroupName = new StringBuilder();
            uint adminGroupNameCapacity = (uint)adminGroupName.Capacity;
            StringBuilder referencedDomainName = new StringBuilder();
            uint referencedDomainNameCapacity = (uint)referencedDomainName.Capacity;
            NativeMethods.SID_NAME_USE eUse;

            // Get the SID of the administrator group.
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] adminGroupSid = new byte[sid.BinaryLength];
            sid.GetBinaryForm(adminGroupSid, 0);
            if (!NativeMethods.LookupAccountSid(
                null,
                adminGroupSid,
                adminGroupName,
                ref adminGroupNameCapacity,
                referencedDomainName,
                ref referencedDomainNameCapacity,
                out eUse))
            {
                FailWithError("LookupAccountSid");
            }
            return adminGroupName.ToString();
        }

        private static byte[] GetUserSid(string userName)
        {
            // Get the newly created user's SID to add the user to the local
            // admins group.
            byte[] userSid = new byte[1024];
            uint userSidLength = (uint)userSid.Length;
            StringBuilder referencedDomainName = new StringBuilder();
            uint referencedDomainNameCapacity = (uint)referencedDomainName.Capacity;
            NativeMethods.SID_NAME_USE peUse;
            if (!NativeMethods.LookupAccountName(
                null,
                userName,
                userSid,
                ref userSidLength,
                referencedDomainName,
                ref referencedDomainNameCapacity,
                out peUse))
            {
                FailWithError("LookupAccountName");
            }
            return userSid;
        }

        private static void AddUserToAdminGroup(string adminGroupName, byte[] userSid)
        {
            // Add the user's SID to local admins group.
            IntPtr userSidNative = Marshal.AllocHGlobal(userSid.Length);
            Marshal.Copy(userSid, 0, userSidNative, (int)userSid.Length);
            NativeMethods.LOCALGROUP_MEMBERS_INFO_0 info0;
            info0.PSID = userSidNative;
            NativeMethods.NetUserRetEnum netUserRet = NativeMethods.NetLocalGroupAddMembers(
                null,
                adminGroupName,
                0,
                ref info0,
                1);
            Marshal.FreeHGlobal(userSidNative);
            if (NativeMethods.NetUserRetEnum.NerrSuccess != netUserRet)
            {
                FailWithError("NetLocalGroupAddMembers", netUserRet: netUserRet);
            }
        }

        private static void AddAdminUser(string userName)
        {
            try
            {
                string adminGroupName = GetAdminGroupName();
                byte[] userSid = GetUserSid(userName);
                AddUserToAdminGroup(adminGroupName, userSid);
            }
            catch (Exception)
            {
                Logger.Info("Username failed to update. Cleaning up...");
                NativeMethods.NetUserDel(null, userName);
                throw;
            }
        }

        private static NativeMethods.USER_INFO_1 GetUserInfo(NetworkCredential credentials)
        {
            NativeMethods.USER_INFO_1 info;
            info.usri1_name = credentials.UserName;
            info.usri1_password = credentials.Password;
            info.usri1_password_age = 0;

            // NetUserAdd must be called with the user priv "UserPrivUser".
            info.usri1_priv = (uint)UserPrivEnum.UserPrivUser;
            info.usri1_home_dir = null;
            info.usri1_comment = null;
            info.usri1_flags = UfScript + UfNormalAccount + UfDontExpirePasswd;
            info.usri1_script_path = null;
            return info;
        }

        /// <summary>
        /// Attempt to create a new user account.
        /// If the user already exists we should reset the password.
        /// </summary>
        /// <param name="userAccount">
        /// The userName of the account to create.
        /// </param>
        private static void CreateAccount(WindowsKey userAccount)
        {
            NativeMethods.NetUserRetEnum netUserRet;
            try
            {
                NetworkCredential credentials = new NetworkCredential();
                credentials.Password = GeneratePassword();
                credentials.UserName = userAccount.UserName;

                Logger.Info("Creating a new user account for {0}.", userAccount.UserName);
                NativeMethods.USER_INFO_1 info = GetUserInfo(credentials);

                // Create a new user account with standard user permissions.
                uint paramErrorIndex;
                netUserRet = NativeMethods.NetUserAdd(null, 1, ref info, out paramErrorIndex);
                if (NativeMethods.NetUserRetEnum.NerrUserExists == netUserRet)
                {
                    Logger.Info("Username already exists. Changing password...");
                    info.usri1_priv = (uint)UserPrivEnum.UserPrivAdmin;
                    netUserRet = NativeMethods.NetUserSetInfo(
                        null,
                        userAccount.UserName,
                        1,
                        ref info,
                        out paramErrorIndex);
                }
                else if (NativeMethods.NetUserRetEnum.NerrSuccess == netUserRet)
                {
                    // Adding user account to the local administrator group.
                    Logger.Info("Adding user...");
                    AddAdminUser(userAccount.UserName);
                }
                if (NativeMethods.NetUserRetEnum.NerrSuccess != netUserRet)
                {
                    throw new Win32Exception((int)netUserRet, netUserRet.ToString());
                }
                PrintEncryptedCredentials(credentials, userAccount.Modulus, userAccount.Exponent);
                Logger.Info("User accounts updated.");
                return;
            }
            catch (Exception e)
            {
                PrintCredentialsJson(
                    passwordFound: false,
                    exponent: userAccount.Exponent,
                    modulus: userAccount.Modulus,
                    userName: userAccount.UserName,
                    error: e.Message);
                throw;
            }
        }

        private void AddUserAccounts(List<WindowsKey> userAccounts)
        {
            foreach (WindowsKey userAccount in userAccounts)
            {
                while (true)
                {
                    try
                    {
                        CreateAccount(userAccount);
                        break;
                    }
                    catch (Win32Exception e)
                    {
                        if ((int)NativeMethods.NetUserRetEnum.NerrPasswordTooShort == e.NativeErrorCode)
                        {
                            Logger.Info("Password reset failed. Retrying...");
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // The user account was created so add a multi-string value to
                // the registry.
                registryWriter.AddMultiStringValue(RegistryKey, userAccount.ToString());
            }
        }

        [PermissionSetAttribute(SecurityAction.Demand, Name = "FullTrust")]
        public void SetMetadata(List<WindowsKey> metadata)
        {
            List<string> registryKeys = registryWriter.GetMultiStringValue(RegistryKey);
            List<WindowsKey> registryWindowsKeys = registryKeys
                .ConvertAll<WindowsKey>(WindowsKey.DeserializeWindowsKey);
            List<WindowsKey> toAdd = new List<WindowsKey>(metadata.Except(registryWindowsKeys));
            List<string> metadataStrings = metadata.ConvertAll<string>(user => user.ToString());
            List<string> toRemoveFromRegistry = new List<string>(registryKeys.Except(metadataStrings));
            AddUserAccounts(toAdd);
            registryWriter.RemoveMultiStringValues(RegistryKey, toRemoveFromRegistry);
        }
    }
}