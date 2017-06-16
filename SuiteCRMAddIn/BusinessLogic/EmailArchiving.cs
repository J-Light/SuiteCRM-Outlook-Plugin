/**
 * Outlook integration for SuiteCRM.
 * @package Outlook integration for SuiteCRM
 * @copyright SalesAgility Ltd http://www.salesagility.com
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU LESSER GENERAL PUBLIC LICENCE as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU LESSER GENERAL PUBLIC LICENCE
 * along with this program; if not, see http://www.gnu.org/licenses
 * or write to the Free Software Foundation,Inc., 51 Franklin Street,
 * Fifth Floor, Boston, MA 02110-1301  USA
 *
 * @author SalesAgility <info@salesagility.com>
 */
namespace SuiteCRMAddIn.BusinessLogic
{
    using Daemon;
    using SuiteCRMClient;
    using SuiteCRMClient.Email;
    using SuiteCRMClient.Exceptions;
    using SuiteCRMClient.Logging;
    using SuiteCRMClient.RESTObjects;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Outlook = Microsoft.Office.Interop.Outlook;
    using SuiteCRMAddIn.Extensions;

    /// <summary>
    /// The agent which handles the automatic and manual archiving of emails.
    /// </summary>
    /// <remarks>
    /// Some of functionality of this class is duplicated in SuiteCRMClient.Email.clsEmailArchive.
    /// TODO: Refactor. See issue #125
    /// </remarks>
    public class EmailArchiving : RepeatingProcess
    {
        private UserSession SuiteCRMUserSession => Globals.ThisAddIn.SuiteCRMUserSession;

        /// <summary>
        /// Magic property tag to get the email address from an Outlook Recipient object.
        /// </summary>
        const string PR_SMTP_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x39FE001E";

        /// <summary>
        /// Canonical format to use when saving date/times to CRM; essentially, ISO8601 without the 'T'.
        /// </summary>
        public const string EmailDateFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>
        /// The name of the Outlook user property on which we will store the CRM Category associated
        /// with an email, of any.
        /// </summary>
        public const string CRMCategoryPropertyName = "SuiteCRMCategory";

        public EmailArchiving(string name, ILogger log) : base(name, log)
        {
        }

        internal override void PerformIteration()
        {
            if (Globals.ThisAddIn.HasCrmUserSession)
            {
                Log.Debug("Auto-Archive iteration started");

                var minReceivedDateTime = DateTime.UtcNow.AddDays(0 - Properties.Settings.Default.DaysOldEmailToAutoArchive);
                var foldersToBeArchived = GetMailFolders(Globals.ThisAddIn.Application.Session.Folders)
                    .Where(FolderShouldBeAutoArchived);

                foreach (var folder in foldersToBeArchived)
                {
                    ArchiveFolderItems(folder, minReceivedDateTime);
                }
                Log.Debug("Auto-Archive iteration completed");
            }
            else
            {
                Log.Debug("Auto-Archive iteration skipped because no user session.");
            }
        }

        private bool FolderShouldBeAutoArchived(Outlook.Folder folder) => FolderShouldBeAutoArchived(folder.EntryID);

        private bool FolderShouldBeAutoArchived(string folderEntryId)
            => Properties.Settings.Default.AutoArchiveFolders?.Contains(folderEntryId) ?? false;

        private void ArchiveFolderItems(Outlook.Folder objFolder, DateTime minReceivedDateTime)
        {
            try
            {
                var unreadEmails = objFolder.Items.Restrict(
                        $"[ReceivedTime] >= \'{minReceivedDateTime.AddDays(-1):yyyy-MM-dd HH:mm}\'");

                for (int i = 1; i <= unreadEmails.Count; i++)
                {
                    var olItem = unreadEmails[i] as Outlook.MailItem;
                    if (olItem != null)
                    {
                        try
                        {
                            olItem.Archive(EmailArchiveReason.Inbound);
                        }
                        catch (Exception any)
                        {
                            Log.Error($"Failed to archive email '{olItem.Subject}' from '{olItem.GetSenderSMTPAddress()}", any);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"EmailArchiving.ArchiveFolderItems; folder {objFolder.Name}:", ex);
            }
        }

        public void ProcessEligibleNewMailItem(Outlook.MailItem olItem, EmailArchiveReason reason, string excludedEmails = "")
        {
            var parentFolder = olItem.Parent as Outlook.Folder;
            if (parentFolder == null)
            {
                Log.Debug($"NULL email folder for {reason} “{olItem.Subject}”");
                return;
            }

            if (EmailShouldBeArchived(reason, parentFolder.Store))
            {
                olItem.Archive(reason, excludedEmails);
            }
            else
            {
                Log.Debug($"NOT archiving {reason} email (folder {parentFolder.Name})");
            }
        }

        private bool EmailShouldBeArchived(EmailArchiveReason type, Outlook.Store store)
        {
            bool result;
            var storeId = store.StoreID;
            switch (type)
            {
                case EmailArchiveReason.Inbound:
                    result = Properties.Settings.Default.AccountsToArchiveInbound != null &&
                        Properties.Settings.Default.AccountsToArchiveInbound.Contains(storeId);
                    break;
                case EmailArchiveReason.Outbound:
                    result = Properties.Settings.Default.AccountsToArchiveOutbound != null &&
                        Properties.Settings.Default.AccountsToArchiveOutbound.Contains(storeId);
                    break;
                default:
                    result = false;
                    break;
            }

            return result;
        }


        /// <summary>
        /// Get the item with this entry id.
        /// </summary>
        /// <param name="entryId">An outlook entry id.</param>
        /// <returns>the requested item, if found.</returns>
        public Outlook.MailItem GetItemById(string entryId)
        {
            return Globals.ThisAddIn.Application.GetNamespace("MAPI").GetItemFromID(entryId);
        }


        private IEnumerable<Outlook.Folder> GetMailFolders(Outlook.Folders root)
        {
            var result = new List<Outlook.Folder>();
            GetMailFoldersHelper(root, result);
            return result;
        }

        private void GetMailFoldersHelper(Outlook.Folders objInpFolders, IList<Outlook.Folder> result)
        {
            try
            {
                foreach (Outlook.Folder objFolder in objInpFolders)
                {
                    if (objFolder.Folders.Count > 0)
                    {
                        result.Add(objFolder);
                        GetMailFoldersHelper(objFolder.Folders, result);
                    }
                    else
                        result.Add(objFolder);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.GetMailFolders", ex);
                ;
            }
        }


        public ArchiveResult ArchiveEmailWithEntityRelationships(Outlook.MailItem olItem, IEnumerable<CrmEntity> selectedCrmEntities, EmailArchiveReason reason)
        {
            var result = olItem.Archive(reason);
            if (result.IsSuccess)
            {
                var warnings = CreateEmailRelationshipsWithEntities(result.EmailId, selectedCrmEntities);
                result = ArchiveResult.Success(
                    result.EmailId,
                    result.Problems == null ?
                    warnings :
                    result.Problems.Concat(warnings));
            }

            return result;
        }

        private IList<System.Exception> CreateEmailRelationshipsWithEntities(string crmMailId, IEnumerable<CrmEntity> selectedCrmEntities)
        {
            var failures = new List<System.Exception>();
            foreach (var entity in selectedCrmEntities)
            {
                try
                {
                    CreateEmailRelationshipOrFail(crmMailId, entity);
                }
                catch (System.Exception failure)
                {
                    Log.Error("CreateEmailRelationshipsWithEntities", failure);
                    failures.Add(failure);
                }
            }
            return failures;
        }

        private void SaveMailItemIfNecessary(Outlook.MailItem olItem, EmailArchiveReason reason)
        {
            if (reason == EmailArchiveReason.SendAndArchive)
            {
                olItem.Save();
            }
        }


        public void CreateEmailRelationshipOrFail(string emailId, CrmEntity entity)
        {
            var success = clsSuiteCRMHelper.TrySetRelationship(
                new eSetRelationshipValue
                {
                    module2 = "emails",
                    module2_id = emailId,
                    module1 = entity.ModuleName,
                    module1_id = entity.EntityId,
                }, Objective.Email);

            if (!success) throw new CrmSaveDataException($"Cannot create email relationship with {entity.ModuleName} ('set_relationship' failed)");
        }
    }
}
