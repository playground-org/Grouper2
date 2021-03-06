﻿using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Grouper2.SddlParser;
using Newtonsoft.Json.Linq;

namespace Grouper2
{
    class LDAPstuff
    {

        const int NO_ERROR = 0;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        enum SID_NAME_USE
        {
            SidTypeUser = 1,
            SidTypeGroup,
            SidTypeDomain,
            SidTypeAlias,
            SidTypeWellKnownGroup,
            SidTypeDeletedAccount,
            SidTypeInvalid,
            SidTypeUnknown,
            SidTypeComputer
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool LookupAccountSid(
            string lpSystemName,
            [MarshalAs(UnmanagedType.LPArray)] byte[] Sid,
            StringBuilder lpName,
            ref uint cchName,
            StringBuilder referencedDomainName,
            ref uint cchReferencedDomainName,
            out SID_NAME_USE peUse);

        public static string GetUserFromSid(string sidString)
        {
            // stolen wholesale from http://www.pinvoke.net/default.aspx/advapi32.LookupAccountSid

            StringBuilder name = new StringBuilder();
            uint cchName = (uint)name.Capacity;
            StringBuilder referencedDomainName = new StringBuilder();
            uint cchReferencedDomainName = (uint)referencedDomainName.Capacity;
            SID_NAME_USE sidUse;
            SecurityIdentifier sidObj = new SecurityIdentifier(sidString);
            byte[] sidBytes = new byte[sidObj.BinaryLength];
            sidObj.GetBinaryForm(sidBytes, 0);
            int err = NO_ERROR;
            if (!LookupAccountSid(null, sidBytes, name, ref cchName, referencedDomainName, ref cchReferencedDomainName, out sidUse))
            {
                err = Marshal.GetLastWin32Error();
                if (err == ERROR_INSUFFICIENT_BUFFER)
                {
                    name.EnsureCapacity((int)cchName);
                    referencedDomainName.EnsureCapacity((int)cchReferencedDomainName);
                    err = NO_ERROR;
                    if (!LookupAccountSid(null, sidBytes, name, ref cchName, referencedDomainName, ref cchReferencedDomainName, out sidUse))
                        err = Marshal.GetLastWin32Error();
                }
            }
            if ((err != 0) && GlobalVar.DebugMode)
                Utility.DebugWrite(@"Error : " + err);

            string lookupResult = "";
            if (referencedDomainName.ToString().Length > 0)
            {
                lookupResult = referencedDomainName.ToString() + "\\" + name.ToString();
            }
            else
            {
                lookupResult = name.ToString();
            } 

            return lookupResult;
        }

        public static JObject GetDomainGpos()
        {
            try
            {
                DirectoryEntry rootDse = new DirectoryEntry();
                DirectoryEntry root = new DirectoryEntry();
                DirectoryEntry rootExtRightsContext = new DirectoryEntry();
                if (GlobalVar.UserDefinedDomainDn != null)
                {
                    rootDse = new DirectoryEntry(("LDAP://" + GlobalVar.UserDefinedDomain + "/rootDSE"), GlobalVar.UserDefinedUsername, GlobalVar.UserDefinedPassword);
                    root = new DirectoryEntry(("GC://" + rootDse.Properties["defaultNamingContext"].Value),
                        GlobalVar.UserDefinedUsername, GlobalVar.UserDefinedPassword);
                    string schemaContextString = rootDse.Properties["schemaNamingContext"].Value.ToString();
                    rootExtRightsContext =
                        new DirectoryEntry("LDAP://" + schemaContextString.Replace("Schema", "Extended-Rights"),
                            GlobalVar.UserDefinedUsername, GlobalVar.UserDefinedPassword);
                }
                else
                {
                    rootDse = new DirectoryEntry("LDAP://rootDSE");
                    root = new DirectoryEntry("GC://" + rootDse.Properties["defaultNamingContext"].Value);
                    string schemaContextString = rootDse.Properties["schemaNamingContext"].Value.ToString();
                    rootExtRightsContext =
                        new DirectoryEntry("LDAP://" + schemaContextString.Replace("Schema", "Extended-Rights"));
                }
            
                // make a searcher to find GPOs
                DirectorySearcher gpoSearcher = new DirectorySearcher(root)
                {
                    Filter = "(objectClass=groupPolicyContainer)",
                    SecurityMasks = SecurityMasks.Dacl | SecurityMasks.Owner
                };

                SearchResultCollection gpoSearchResults = gpoSearcher.FindAll();
                /*
            // stolen from prashant - grabbing guids for extended rights
            Dictionary<string, string> guidDict = new Dictionary<string, string>
            {
                {"00000000-0000-0000-0000-000000000000", "All"}
            };

            // and again where we grab all the Extended Rights
            DirectorySearcher rightsSearcher = new DirectorySearcher(rootExtRightsContext)
            {
                Filter = "(objectClass=controlAccessRight)",
                PropertiesToLoad = {"name", "rightsGUID"}
            };

            SearchResultCollection extRightsResultCollection = rightsSearcher.FindAll();

            foreach (SearchResult extRightsResult in extRightsResultCollection)
            {
                string extRightGuidString = extRightsResult.Properties["rightsguid"][0].ToString();
                string extRightNameString = extRightsResult.Properties["name"][0].ToString();
                // for some reason we hit a single duplicate in this lot. nfi what that's about. TODO - figure that out.
                try
                {
                    guidDict.Add(extRightGuidString, extRightNameString);
                }
                catch (System.ArgumentException)
                {
                    if (GlobalVar.DebugMode)
                    {
                        Utility.DebugWrite("Hit a duplicate GUID in extRightsResult");
                    }
                }
            }*/

                // new dictionary for data from each GPO to go into
                JObject gposData = new JObject();

                foreach (SearchResult gpoSearchResult in gpoSearchResults)
                {
                    // object for all data for this one gpo
                    JObject gpoData = new JObject();
                    DirectoryEntry gpoDe = gpoSearchResult.GetDirectoryEntry();
                    // get some useful attributes of the gpo
                    string gpoDispName = gpoDe.Properties["displayName"].Value.ToString();
                    gpoData.Add("Display Name", gpoDispName);
                    string gpoUid = gpoDe.Properties["name"].Value.ToString();
                    // this is to catch duplicate UIDs caused by Default Domain Policy and Domain Controller Policy having 'well known guids'
                    if (gposData[gpoUid] != null)
                    {
                        Utility.DebugWrite("\nI think you're in a multi-domain environment cos I just saw two GPOs with the same GUID. You should be careful not to miss stuff in the Default Domain Policy and Default Domain Controller Policy.");
                        continue;
                    }
                    gpoData.Add("UID", gpoUid);
                    string gpoDn = gpoDe.Properties["distinguishedName"].Value.ToString();
                    gpoData.Add("Distinguished Name", gpoDn);
                    string gpoCreated = gpoDe.Properties["whenCreated"].Value.ToString();
                    gpoData.Add("Created", gpoCreated);

                    // 3= all disabled
                    // 2= computer configuration settings disabled
                    // 1= user policy disabled
                    // 0 = all enabled
                    string gpoFlags = gpoDe.Properties["flags"].Value.ToString();
                    string gpoEnabledStatus = "";
                    switch (gpoFlags)
                    {
                        case "0":
                            gpoEnabledStatus = "Enabled";
                            break;
                        case "1":
                            gpoEnabledStatus = "User Policy Disabled";
                            break;
                        case "2":
                            gpoEnabledStatus = "Computer Policy Disabled";
                            break;
                        case "3":
                            gpoEnabledStatus = "Disabled";
                            break;
                        default:
                            gpoEnabledStatus = "Couldn't process GPO Enabled Status. Weird.";
                            break;
                    }
                    gpoData.Add("GPO Status", gpoEnabledStatus);
                    // get the acl
                    ActiveDirectorySecurity gpoAcl = gpoDe.ObjectSecurity;
                    // // Get the owner in a really dumb way
                    // string gpoSddl = gpoAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Owner);
                    // JObject parsedOwner = ParseSDDL.ParseSddlString(gpoSddl, SecurableObjectType.DirectoryServiceObject);
                    // string gpoOwner = parsedOwner["Owner"].ToString();
                    // gpoData.Add("Owner", gpoOwner);
                    // make a JObject to put the stuff in
                    JObject gpoAclJObject = new JObject();

                    AccessControlSections sections = AccessControlSections.All;
                    string sddlString = gpoAcl.GetSecurityDescriptorSddlForm(sections);
                    JObject parsedSDDL = ParseSddl.ParseSddlString(sddlString, SecurableObjectType.DirectoryServiceObject);
                
                    foreach (KeyValuePair<string, JToken> thing in parsedSDDL)
                    {
                        if (thing.Key == "Owner")
                        {
                            gpoAclJObject.Add("Owner", thing.Value.ToString());
                            continue;
                        }

                        if (thing.Key == "Group")
                        {
                            gpoAclJObject.Add("Group", thing.Value);
                            continue;
                        }

                        if (thing.Key == "DACL")
                        {
                            foreach (JProperty ace in thing.Value.Children())
                            {
                                int aceInterestLevel = 1;
                                bool interestingRightPresent = false;
                                if (ace.Value["Rights"] != null)
                                {
                                    string[] intRightsArray0 = new string[]
                                    {
                                        "WRITE_OWNER", "CREATE_CHILD", "WRITE_PROPERTY", "WRITE_DAC", "SELF_WRITE", "CONTROL_ACCESS"
                                    };

                                    foreach (string right in intRightsArray0)
                                    {
                                        if (ace.Value["Rights"].Contains(right))
                                        {
                                            interestingRightPresent = true;
                                        }
                                    }
                                }

                                string trusteeSid = ace.Value["SID"].ToString();
                                string[] boringSidEndings = new string[]
                                    {"-3-0", "-5-9", "5-18", "-512", "-519", "SY", "BA", "DA", "CO", "ED", "PA", "CG", "DD", "EA", "LA",};
                                string[] interestingSidEndings = new string[]
                                    {"DU", "WD", "IU", "BU", "AN", "AU", "BG", "DC", "DG", "LG"};
                            
                                bool boringUserPresent = false;
                                foreach (string boringSidEnding in boringSidEndings)
                                {
                                    if (trusteeSid.EndsWith(boringSidEnding))
                                    {
                                        boringUserPresent = true;
                                        break;
                                    }
                                }

                                bool interestingUserPresent = false;
                                foreach (string interestingSidEnding in interestingSidEndings)
                                {
                                    if (trusteeSid.EndsWith(interestingSidEnding))
                                    {
                                        interestingUserPresent = true;
                                        break;
                                    }
                                }

                                if (interestingUserPresent && interestingRightPresent)
                                {
                                    aceInterestLevel = 10;
                                }
                                else if (boringUserPresent)
                                {
                                    aceInterestLevel = 0;
                                }

                                if (aceInterestLevel >= GlobalVar.IntLevelToShow)
                                {
                                    // pass the whole thing on
                                    gpoAclJObject.Add(ace);
                                }
                            }
                        }

                    }
                

                    //add the JObject to our blob of data about the gpo
                    if (gpoAclJObject.HasValues)
                    {
                        gpoData.Add("ACLs", gpoAclJObject);
                    }
                
                    gposData.Add(gpoUid, gpoData);
                }
        
        
                return gposData;
            }
            catch (Exception exception)
            {
                Utility.DebugWrite(exception.ToString());
                Console.ReadKey();
                Environment.Exit(1);
            }

            return null;
        }
    
        public static string GetDomainSid()
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            string domainSid = id.User.AccountDomainSid.ToString();
            return domainSid;
        }
        /*
    public static string GetUserFromSid(string sid)
    {
        string account = "Failed to resolve SID";
        try
        {
            account = new System.Security.Principal.SecurityIdentifier(sid)
                .Translate(typeof(System.Security.Principal.NTAccount)).ToString();
        }
        catch (Exception e)
        {
            //Utility.DebugWrite(e.ToString());
            Utility.DebugWrite("Failed to resolve SID: " + sid);
        }

        return account;
    }
    */




    
    }
}
