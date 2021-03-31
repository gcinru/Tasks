namespace Terrasoft.Configuration
{
	using Newtonsoft.Json;

	using System;
    using System.Data;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.ServiceModel;
    using System.ServiceModel.Activation;
	using System.ServiceModel.Web;
	using System.Text;

	using Terrasoft.Web.Common;
    using Terrasoft.Core;
    using Terrasoft.Core.Entities;
    using System.Collections.Generic;
    using Terrasoft.Core.DB;
	using Terrasoft.Common;


	[ServiceContract]
	[AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Required)]
	public class GKILicensingLDAPService : BaseService
	{
		public GKILicensingLDAPService() { }
		public GKILicensingLDAPService(UserConnection userConnection)
		{
			UserConnection = userConnection;
		}
		public static CookieContainer AuthCookies;

		/// <summary>
		/// Синхронизация с MS AD
		/// </summary>
		/// <returns> описание ошибок </returns>
		public string GKILDAPSync()
		{
			string fullReport;
			string groupReport;
			List<Guid> ldapUserIds = new List<Guid>();
			List<KeyValuePair<Guid, Guid>> ldapInstanceUserIds = new List<KeyValuePair<Guid, Guid>>(); //instanceId, UserId
			Dictionary<Guid, List<KeyValuePair<Guid, Guid>>> ldapInstanceUserLicPackages = new Dictionary<Guid, List<KeyValuePair<Guid, Guid>>>(); //UserId, instanceId, LicPackage

			try
			{
				#region groupSelect
				var groupSelect =
					new Select(UserConnection)
						.Column("Id")
						.Column("Name")
					.From("GKIGroupAD")
					.Where("Id").In(new Select(UserConnection)
						.Column("GKIGroupADId").From("GKIInstanceGroupAD").Where("GKIInstanceId").Not().IsNull()) as Select;
				var groupList = new List<GKILicensingLdapGroup>();
				using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
				{
					using (IDataReader dr = groupSelect.ExecuteReader(dbExecutor))
					{
						while (dr.Read())
						{
							Guid groupId = dr.GetGuid(0);
							string groupDn = dr.GetValue(1).ToString();
							groupList.Add(new GKILicensingLdapGroup(groupId, groupDn));
						}
					}
				}
				fullReport = String.Concat("Groups count: ", groupList.Count.ToString());

				#endregion

				#region all GKIGroupADUsers
				var allGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKIInstanceId")
							.Column("GKILicUserId")
							.Column("GKIGroupADId")
							.From("GKIGroupADUsers") as Select;

				var allGKIInstanceGroupADUsersList = new Dictionary<Guid, List<KeyValuePair<Guid, Guid>>>(); //instance, user, group

				using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
				{
					using (IDataReader dr = allGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
					{
						while (dr.Read())
						{
							if (!allGKIInstanceGroupADUsersList.ContainsKey(dr.GetGuid(0)))
							{
								allGKIInstanceGroupADUsersList.Add(dr.GetGuid(0), new List<KeyValuePair<Guid, Guid>>());
							}
							var dbGKIGroupADInstance = allGKIInstanceGroupADUsersList[dr.GetGuid(0)];
							if (dbGKIGroupADInstance.FindAll(x =>
								x.Key == dr.GetGuid(1) &&
								x.Value == dr.GetGuid(2)).Count == 0)
							{
								dbGKIGroupADInstance.Add(new KeyValuePair<Guid, Guid>(dr.GetGuid(1), dr.GetGuid(2)));
							}
						}
					}
				}

				#endregion

				string winInstanceUrl = Terrasoft.Core.Configuration.SysSettings.GetValue<string>(
					UserConnection, "GKILicensingWinInstanceUrl", String.Empty);
				if (winInstanceUrl == String.Empty)
				{
					throw new Exception("Empty system setting \"GKILicensingWinInstanceUrl\"");
				}

				DateTime lastUserModifiedOn = DateTime.MinValue;
				int groupCount = 0;
				foreach (var groupItem in groupList)
				{
					groupCount++;

					List<KeyValuePair<Guid, Guid>> ldapGroupRelatedInstanceUserIds = new List<KeyValuePair<Guid, Guid>>(); //instanceId, UserId

					var groupFilter = groupItem.Dn;
					string activeFilter = "(!(userAccountControl:1.2.840.113556.1.4.803:=2))";
					fullReport += String.Concat(". Group: ", groupFilter);
					DateTime? ldapModifiedDate = default(DateTime?); // TODO: filter by modified date for performance reasons. methods CheckUsersInGroup and GetMinModifiedDateOfNewUsers
					string modifiedUsersFilter = GetUserFilterWithMinModifiedOnAttributeOrDate(ldapModifiedDate); // all users for now
					var filter = "(&(&" + modifiedUsersFilter + activeFilter + ")" + groupFilter + ")";
					fullReport += String.Concat(". Filter: ", filter);
					groupReport = String.Concat("Filter: ", filter);

					List<LDAP.LdapUser> ldapUsers;

					if (UserConnection.GetIsFeatureEnabled("GKILicensingFeatureTestMode"))
					{
						ldapUsers = new List<LDAP.LdapUser>();
						var usersSelect =
						new Select(UserConnection)
							.Column("GKIName")
						.From("GKILdapTestUsers")
						.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id)) as Select;
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = usersSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									ldapUsers.Add(new LDAP.LdapUser { Name = dr.GetValue(0).ToString(), IsActive = true });
								}
							}
						}

					}
					else
					{
						SyncLDAPResponse syncLDAPResponse = JsonConvert.DeserializeObject<SyncLDAPResponse>(
							GKILicenseLDAPHttpRequest(winInstanceUrl, GKILicensingConstantsCs.LicensingLDAP.GKIGetSyncLDAPResponseUrl, JsonConvert.SerializeObject(filter)));
						if (syncLDAPResponse.Success != true)
                        {
							throw new Exception(String.Concat("Windows instance error: ", syncLDAPResponse.ErrMsg));
						}
						ldapUsers = syncLDAPResponse.LdapUsers;
						fullReport += String.Concat(". Users acquired from AD: ", ldapUsers.Count);
						groupReport += String.Concat(". Users acquired from AD: ", ldapUsers.Count);
					}

					int usersAdded = 0;
					int usersUpdated = 0;
					List<string> ldapGroupUserNames = new List<string>();

					#region existing and new users
					foreach (var user in ldapUsers)
					{
						if (string.IsNullOrEmpty(user.Name) || !user.IsActive)
						{
							continue;
						}
						if (lastUserModifiedOn < user.ModifiedOn)
						{
							lastUserModifiedOn = user.ModifiedOn;
						}

						#region GKILicUser GKIInstanceLicUser GKIGroupAD

						var gkiLicUserSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKILicUser");
						var gkiInstanceLicUserSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIInstanceLicUser");

						#region GKILicUser
						//search for the user in GKILicUser
						var gkiLicUserQuery = new EntitySchemaQuery(gkiLicUserSchema);
						gkiLicUserQuery.UseAdminRights = false;
						gkiLicUserQuery.AddAllSchemaColumns();
						gkiLicUserQuery.Filters.Add(gkiLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIName", user.Name));
						var gkiLicUserCollection = gkiLicUserQuery.GetEntityCollection(UserConnection);
						Entity gkiLicUserEntity = gkiLicUserSchema.CreateEntity(UserConnection);
						if (gkiLicUserCollection.Count < 1)
						{
							//create if it's not found
							gkiLicUserEntity.SetDefColumnValues();
							gkiLicUserEntity.SetColumnValue("GKIName", user.Name);
							gkiLicUserEntity.SetColumnValue("GKIMSADLogin", user.Name);
							gkiLicUserEntity.SetColumnValue("GKIMSADActive", true);
							gkiLicUserEntity.Save();
							usersAdded++;

							//GKIInstanceLicUser inserting is down below in "searching user in GKIInstanceLicUser records"
						}
						else
						{
							gkiLicUserEntity = gkiLicUserCollection.First();
							gkiLicUserEntity.SetColumnValue("GKIMSADActive", true);
							gkiLicUserEntity.SetColumnValue("GKIMSADLogin", user.Name);
							usersUpdated++;
						}
						gkiLicUserEntity.Save();

						ldapUserIds.Add(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")); // ksenzov: yeah, I know that might be doubles in it, but we use it only for comparison reasons so it doesn't matter

						#endregion

						#region searching user in GKIInstanceLicUser records
						//selecting all instances that are related to this group
						var groupInstancesSelect =
							new Select(UserConnection)
								.Column("GKIInstanceId")
							.From("GKIInstanceGroupAD")
							.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id)) as Select;
						var groupInstancesList = new List<Guid>();
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = groupInstancesSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									groupInstancesList.Add(dr.GetGuid(0));
								}
							}
						}
						groupInstancesList = groupInstancesList.Distinct().ToList();
						foreach (Guid instanceId in groupInstancesList)
						{
							if (ldapInstanceUserIds.FindAll(x =>
									x.Key == instanceId &&
									x.Value == gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"))
								.Count == 0)
							{
								ldapInstanceUserIds.Add(new KeyValuePair<Guid, Guid>(instanceId, gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							}

							if (ldapGroupRelatedInstanceUserIds.FindAll(x =>
									x.Key == instanceId &&
									x.Value == gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"))
								.Count == 0)
							{
								ldapGroupRelatedInstanceUserIds.Add(new KeyValuePair<Guid, Guid>(instanceId, gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							}

							//GKIInstanceLicUser
							var gkiInstanceLicUserQuery = new EntitySchemaQuery(gkiInstanceLicUserSchema);
							gkiInstanceLicUserQuery.UseAdminRights = false;
							gkiInstanceLicUserQuery.AddAllSchemaColumns();
							gkiInstanceLicUserQuery.Filters.Add(gkiInstanceLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							gkiInstanceLicUserQuery.Filters.Add(gkiInstanceLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance",
									instanceId));
							var gkiInstanceLicUserCollection = gkiInstanceLicUserQuery.GetEntityCollection(UserConnection);
							Entity gkiInstanceLicUserEntity = gkiInstanceLicUserSchema.CreateEntity(UserConnection);
							if (gkiInstanceLicUserCollection.Count < 1)
							{
								gkiInstanceLicUserEntity.SetDefColumnValues();
								gkiInstanceLicUserEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
								gkiInstanceLicUserEntity.SetColumnValue("GKILicSyncSourceId", GKILicensingConstantsCs.GKILicSyncSource.MSAD);
								gkiInstanceLicUserEntity.SetColumnValue("GKIActive", false);
								gkiInstanceLicUserEntity.SetColumnValue("GKIMSADActive", true);
								gkiInstanceLicUserEntity.SetColumnValue("GKIInstanceId", instanceId);
							}
							else
							{
								gkiInstanceLicUserEntity = gkiInstanceLicUserCollection.First();
								gkiInstanceLicUserEntity.SetColumnValue("GKIMSADActive", true);
							}
							gkiInstanceLicUserEntity.Save();

							#endregion

							#region GKIGroupAD

							//searching for existing GKIGroupAD
							var esqGKIGroupADSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIGroupADUsers");

							var esqGKIGroupADQuery = new EntitySchemaQuery(esqGKIGroupADSchema);
							esqGKIGroupADQuery.UseAdminRights = false;
							esqGKIGroupADQuery.AddAllSchemaColumns();
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance",
									instanceId));
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIGroupAD",
									groupItem.Id));
							var esqGKIGroupADQueryCollection = esqGKIGroupADQuery.GetEntityCollection(UserConnection);
							Entity esqGKIGroupADEntity = esqGKIGroupADSchema.CreateEntity(UserConnection);
							if (esqGKIGroupADQueryCollection.Count < 1)
							{
								esqGKIGroupADEntity.SetDefColumnValues();
								esqGKIGroupADEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
								esqGKIGroupADEntity.SetColumnValue("GKIInstanceId", instanceId);
								esqGKIGroupADEntity.SetColumnValue("GKIGroupADId", groupItem.Id);
								esqGKIGroupADEntity.Save();
							}
							else
							{
								esqGKIGroupADEntity = esqGKIGroupADQueryCollection.First();
							}

							#endregion

							#region GKILicUserInstanceLicPackage


							var esqGKIGroupADInstanceLicenseSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIGroupADInstanceLicense");
							var esqGKILicUserInstanceLicPackageSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKILicUserInstanceLicPackage");

							//creating a record for each of GroupAD's products if it's needed
							//if (gkiLicUserEntity.GetTypedColumnValue<bool>("GKIMSADActive")): it is always active because of the logic in GKILicUser above
							var esqGKIGroupADInstanceLicenseQuery = new EntitySchemaQuery(esqGKIGroupADInstanceLicenseSchema);
							esqGKIGroupADInstanceLicenseQuery.UseAdminRights = false;
							esqGKIGroupADInstanceLicenseQuery.AddAllSchemaColumns();
							esqGKIGroupADInstanceLicenseQuery.Filters.Add(esqGKIGroupADInstanceLicenseQuery.CreateFilterWithParameters(
								FilterComparisonType.Equal,
								"GKIGroupAD",
								groupItem.Id));
							esqGKIGroupADInstanceLicenseQuery.Filters.Add(esqGKIGroupADInstanceLicenseQuery.CreateFilterWithParameters(
								FilterComparisonType.Equal,
								"GKIInstance",
								instanceId));
							var esqGKIGroupADInstanceLicenseQueryCollection = esqGKIGroupADInstanceLicenseQuery.GetEntityCollection(UserConnection);
							foreach (Entity esqGKIGroupADInstanceLicenseEntity in esqGKIGroupADInstanceLicenseQueryCollection)
							{
								//for inactivating missing licenses later
								if (!ldapInstanceUserLicPackages.ContainsKey(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")))
								{
									ldapInstanceUserLicPackages.Add(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"), new List<KeyValuePair<Guid, Guid>>());
								}
								var ldapInstanceUserLicPackage = ldapInstanceUserLicPackages[gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")];
								if (ldapInstanceUserLicPackage.FindAll(x =>
									x.Key == instanceId &&
									x.Value == esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")).Count == 0)
								{
									ldapInstanceUserLicPackage.Add(new KeyValuePair<Guid, Guid>(instanceId, esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")));
								}

								var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(esqGKILicUserInstanceLicPackageSchema);
								esqGKILicUserInstanceLicPackage.UseAdminRights = false;
								esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKIInstance",
									instanceId));
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKILicPackage",
									esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")));
								var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
								if (esqGKILicUserInstanceLicPackageCollection.Count == 0)
								{
									Entity esqGKILicUserInstanceLicPackageEntity = esqGKILicUserInstanceLicPackageSchema.CreateEntity(UserConnection);
									esqGKILicUserInstanceLicPackageEntity.SetDefColumnValues();
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIInstanceId", instanceId);
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKILicPackageId", esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId"));
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
									esqGKILicUserInstanceLicPackageEntity.Save();
								}
							}

							#endregion
						}
						#endregion


					}
					#endregion

					#region group missing users
					List<Guid> groupRelatedInstancesSelect = (from kvp in ldapGroupRelatedInstanceUserIds select kvp.Key).Distinct().ToList();
					foreach (Guid instance in groupRelatedInstancesSelect)
					{
						var existingGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIGroupADUsers")
							.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id))
							.And("GKIInstanceId").IsEqual(Column.Parameter(instance)) as Select;
						var existingGKIGroupADUsersList = new List<Guid>();
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = existingGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									existingGKIGroupADUsersList.Add(dr.GetGuid(0));
								}
							}
						}

						var ldapGroupRelatedInstanceUserIdsList = (from kvp in ldapGroupRelatedInstanceUserIds where kvp.Key == instance select kvp.Value).ToList();
						IEnumerable<Guid> ldapGroupRelatedInstanceUserIdsEnum = ldapGroupRelatedInstanceUserIdsList.AsEnumerable();

						var missingGroupRelatedInstanceUsers = existingGKIGroupADUsersList.Except(ldapGroupRelatedInstanceUserIdsEnum);
						IEnumerable<QueryParameter> missingGroupUsersQueryParameter = missingGroupRelatedInstanceUsers.Select(x => new QueryParameter(x)).ToArray();
						if (missingGroupRelatedInstanceUsers.Count() > 0)
						{
							try
							{
								Delete requestRecordsDelete = new Delete(UserConnection)
									.From("GKIGroupADUsers")
									.Where("GKILicUserId")
									.In(missingGroupUsersQueryParameter)
									.And("GKIInstanceId").IsEqual(Column.Parameter(instance))
									.And("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id))
								as Delete;
								requestRecordsDelete.Execute();

								fullReport += String.Concat(". Group ", groupItem.Dn, "users deleted: ", missingGroupRelatedInstanceUsers.Count().ToString());
								groupReport += String.Concat(". Users deleted: ", missingGroupRelatedInstanceUsers.Count().ToString());
							}
							catch (Exception ex)
							{
								fullReport += String.Concat(". Group ", groupItem.Dn, "users deletion error ", ex.Message);
								groupReport += ". Users deletion error occured";
							}
						}

					}
					#endregion

					fullReport += String.Concat(". Users created: ", usersAdded.ToString(), ". Users updated: ", usersUpdated.ToString());
					groupReport += String.Concat(". Users created: ", usersAdded.ToString(), ". Users updated: ", usersUpdated.ToString());

					//write down a report into a group record
					Update updateReport = new Update(UserConnection, "GKIGroupAD")
						.Set("GKIReport", Column.Parameter(groupReport))
						.Where("Id")
						.IsEqual(Column.Parameter(groupItem.Id))
						as Update;
					int updateReportSuccess = updateReport.Execute();
				}

				#region instance missing users
				List<Guid> instancesSelect = (from kvp in ldapInstanceUserIds select kvp.Key).Distinct().ToList();
				foreach (Guid instance in instancesSelect)
				{
					var existingInstanceUsersSelect =
					new Select(UserConnection)
						.Column("Id")
						.From("GKILicUser")
						.Where("Id").In(new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIInstanceLicUser")
							.Where("GKIInstanceId").IsEqual(Column.Parameter(instance))) as Select;
					var existingInstanceUsersList = new List<Guid>();
					using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
					{
						using (IDataReader dr = existingInstanceUsersSelect.ExecuteReader(dbExecutor))
						{
							while (dr.Read())
							{
								existingInstanceUsersList.Add(dr.GetGuid(0));
							}
						}
					}

					var ldapInstanceUserIdsList = (from kvp in ldapInstanceUserIds where kvp.Key == instance select kvp.Value).ToList();
					IEnumerable<Guid> ldapInstanceUserIdsEnum = ldapInstanceUserIdsList.AsEnumerable();

					var missingInstanceUsers = existingInstanceUsersList.Except(ldapInstanceUserIdsEnum);
					IEnumerable<QueryParameter> missingInstanceUsersQueryParameter = missingInstanceUsers.Select(x => new QueryParameter(x)).ToArray();
					if (missingInstanceUsers.Count() > 0)
					{
						Update updateMissingInstanceUsers = new Update(UserConnection, "GKIInstanceLicUser")
							.Set("GKIMSADActive", Column.Parameter(false))
							.Where("GKILicUserId").In(missingInstanceUsersQueryParameter)
							.And("GKIInstanceId").IsEqual(Column.Parameter(instance))
						as Update;
						updateMissingInstanceUsers.Execute();
					}
					fullReport += String.Concat(". Instance ", instance.ToString(), "users missing: ", missingInstanceUsers.Count().ToString());
				}
				#endregion

				#region missing users
				var existingUsersSelect =
					new Select(UserConnection)
						.Column("Id")
					.From("GKILicUser") as Select;
				var existingUsersList = new List<Guid>();
				using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
				{
					using (IDataReader dr = existingUsersSelect.ExecuteReader(dbExecutor))
					{
						while (dr.Read())
						{
							existingUsersList.Add(dr.GetGuid(0));
						}
					}
				}

				var missingUsers = existingUsersList.Except(ldapUserIds);
				IEnumerable<QueryParameter> missingUsersQueryParameter = missingUsers.Select(x => new QueryParameter(x)).ToArray();
				if (missingUsers.Count() > 0)
				{
					Update updateMissingUsers = new Update(UserConnection, "GKILicUser")
						.Set("GKIMSADActive", Column.Parameter(false))
						.Where("Id").In(missingUsersQueryParameter)
						as Update;
					updateMissingUsers.Execute();
				}

				#endregion

				#region missing licenses
				foreach (var user in ldapInstanceUserLicPackages)
				{
					var instanceList = (from kvp in user.Value select kvp.Key).Distinct().ToList();
					foreach (var instanceId in instanceList)
					{
						var licenseList = (from kvp in user.Value where kvp.Key == instanceId select kvp.Value).Distinct().ToArray();
						object[] licenseParams = licenseList.Cast<object>().ToArray();
						//esq because we need to trigger the event layer
						var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(UserConnection.EntitySchemaManager, "GKILicUserInstanceLicPackage");
						esqGKILicUserInstanceLicPackage.UseAdminRights = false;
						esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser", user.Key));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance", instanceId));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIActive", true));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.NotEqual, "GKILicPackage", licenseParams));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKIInstance:GKIInstance].GKILicPackage"));

						var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
						foreach (var esqGKILicUserInstanceLicPackageEntity in esqGKILicUserInstanceLicPackageCollection)
						{
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivatedBySync", true);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivationReasonLookupId", GKILicensingConstantsCs.GKIDeactivationReasonLookup.LDAP);
							esqGKILicUserInstanceLicPackageEntity.Save();
						}
					}
				}

				#endregion

				#region missing instance users licenses
				foreach (Guid instance in allGKIInstanceGroupADUsersList.Keys)
				{
					var afterGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIGroupADUsers")
							.Where("GKIInstanceId").IsEqual(Column.Parameter(instance)) as Select;
					var afterList = new List<Guid>();
					using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
					{
						using (IDataReader dr = afterGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
						{
							while (dr.Read())
							{
								afterList.Add(dr.GetGuid(0));
							}
						}
					}

					var beforeList = (from kvp in allGKIInstanceGroupADUsersList[instance] select kvp.Key).Distinct();
					IEnumerable<Guid> afterListEnum = afterList.AsEnumerable();

					var missingInstanceADUsers = beforeList.Except(afterListEnum);
					if (missingInstanceADUsers.Count() > 0)
					{
						object[] missingInstanceADUsersParams = missingInstanceADUsers.Cast<object>().ToArray();
						//esq because we need to trigger the event layer
						var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(UserConnection.EntitySchemaManager, "GKILicUserInstanceLicPackage");
						esqGKILicUserInstanceLicPackage.UseAdminRights = false;
						esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser", missingInstanceADUsersParams));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance", instance));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIActive", true));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKIInstance:GKIInstance].Id"));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKILicPackage:GKILicPackage].Id"));

						var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
						foreach (var esqGKILicUserInstanceLicPackageEntity in esqGKILicUserInstanceLicPackageCollection)
						{
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivatedBySync", true);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivationReasonLookupId", GKILicensingConstantsCs.GKIDeactivationReasonLookup.LDAP);
							esqGKILicUserInstanceLicPackageEntity.Save();
						}
					}
				}

				#endregion

				DateTime? maxModificationDateOfLDAPEntry = lastUserModifiedOn > DateTime.MinValue ? lastUserModifiedOn :
						default(DateTime?);
				if (maxModificationDateOfLDAPEntry.HasValue && Core.Configuration.SysSettings.Exists(UserConnection, "GKILicensingLDAPEntryMaxModifiedOn"))
				{
					Core.Configuration.SysSettings.SetDefValue(UserConnection, "GKILicensingLDAPEntryMaxModifiedOn", maxModificationDateOfLDAPEntry.Value);
				}

				var adminService = new GKILicensingAdminService(UserConnection);
				adminService.GKISlaveAndADNotInSync();
			}
            catch(Exception ex)
            {
				string errSubject = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.ExceptionRemindingSubject.Value");
				string errDescription = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.ExceptionRemindingDescription.Value");
				RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKILicensingLDAPSyncProcess", errSubject, errDescription);
				throw ex;
			}

			string subject = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.SuccessRemindingSubject.Value");
			string description = new LocalizableString(UserConnection.Workspace.ResourceStorage,
				"GKILicensingLDAPService",
				"LocalizableStrings.SuccessRemindingDescription.Value");
			RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKILicensingLDAPSyncProcess", subject, description);
			return fullReport;
		}

		/// <summary>
		/// Синхронизация экземпляра с MS AD
		/// </summary>
		/// <param name="filterInstanceId">экземпляр</param> 
		/// <returns></returns>
		public string GKILDAPSelectedSync(Guid filterInstanceId)
		{
			string fullReport;
			string groupReport;
			List<Guid> ldapUserIds = new List<Guid>();
			List<KeyValuePair<Guid, Guid>> ldapInstanceUserIds = new List<KeyValuePair<Guid, Guid>>(); //instanceId, UserId
			Dictionary<Guid, List<KeyValuePair<Guid, Guid>>> ldapInstanceUserLicPackages = new Dictionary<Guid, List<KeyValuePair<Guid, Guid>>>(); //UserId, instanceId, LicPackage
			
			var selectedInstanceIds = (Select) new Select(UserConnection)
				.Column("GKILicUserId")
				.From("GKIGroupADUsers")
				.Where("GKIInstanceId")
				.IsEqual(Column.Parameter(filterInstanceId));
			using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
			{
				using (IDataReader dr = selectedInstanceIds.ExecuteReader(dbExecutor))
				{
					while (dr.Read())
					{
						Guid userId = dr.GetGuid(0);
						ldapInstanceUserIds.Add(new KeyValuePair<Guid, Guid>(filterInstanceId, userId));
					}
				}
			}

			try
			{
				#region groupSelect
				var groupSelect =
					new Select(UserConnection)
						.Column("Id")
						.Column("Name")
					.From("GKIGroupAD")
					.Where("Id").In(new Select(UserConnection)
						.Column("GKIGroupADId").From("GKIInstanceGroupAD").Where("GKIInstanceId").IsEqual(Column.Parameter(filterInstanceId))) as Select;
				var groupList = new List<GKILicensingLdapGroup>();
				using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
				{
					using (IDataReader dr = groupSelect.ExecuteReader(dbExecutor))
					{
						while (dr.Read())
						{
							Guid groupId = dr.GetGuid(0);
							string groupDn = dr.GetValue(1).ToString();
							groupList.Add(new GKILicensingLdapGroup(groupId, groupDn));
						}
					}
				}
				fullReport = String.Concat("Groups count: ", groupList.Count.ToString());

				#endregion

				#region all GKIGroupADUsers
				var allGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKIInstanceId")
							.Column("GKILicUserId")
							.Column("GKIGroupADId")
							.From("GKIGroupADUsers")
							.Where("GKIInstanceId").IsEqual(Column.Parameter(filterInstanceId)) 
						as Select;

				var allGKIInstanceGroupADUsersList = new Dictionary<Guid, List<KeyValuePair<Guid, Guid>>>(); //instance, user, group

				using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
				{
					using (IDataReader dr = allGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
					{
						while (dr.Read())
						{
							if (!allGKIInstanceGroupADUsersList.ContainsKey(dr.GetGuid(0)))
							{
								allGKIInstanceGroupADUsersList.Add(dr.GetGuid(0), new List<KeyValuePair<Guid, Guid>>());
							}
							var dbGKIGroupADInstance = allGKIInstanceGroupADUsersList[dr.GetGuid(0)];
							if (dbGKIGroupADInstance.FindAll(x =>
								x.Key == dr.GetGuid(1) &&
								x.Value == dr.GetGuid(2)).Count == 0)
							{
								dbGKIGroupADInstance.Add(new KeyValuePair<Guid, Guid>(dr.GetGuid(1), dr.GetGuid(2)));
							}
						}
					}
				}

				#endregion

				string winInstanceUrl = Terrasoft.Core.Configuration.SysSettings.GetValue<string>(
					UserConnection, "GKILicensingWinInstanceUrl", String.Empty);
				if (winInstanceUrl == String.Empty)
				{
					throw new Exception("Empty system setting \"GKILicensingWinInstanceUrl\"");
				}

				DateTime lastUserModifiedOn = DateTime.MinValue;
				int groupCount = 0;
				foreach (var groupItem in groupList)
				{
					groupCount++;

					List<KeyValuePair<Guid, Guid>> ldapGroupRelatedInstanceUserIds = new List<KeyValuePair<Guid, Guid>>(); //instanceId, UserId

					var groupFilter = groupItem.Dn;
					string activeFilter = "(!(userAccountControl:1.2.840.113556.1.4.803:=2))";
					fullReport += String.Concat(". Group: ", groupFilter);
					DateTime? ldapModifiedDate = default(DateTime?); // TODO: filter by modified date for performance reasons. methods CheckUsersInGroup and GetMinModifiedDateOfNewUsers
					string modifiedUsersFilter = GetUserFilterWithMinModifiedOnAttributeOrDate(ldapModifiedDate); // all users for now
					var filter = "(&(&" + modifiedUsersFilter + activeFilter + ")" + groupFilter + ")";
					fullReport += String.Concat(". Filter: ", filter);
					groupReport = String.Concat("Filter: ", filter);

					List<LDAP.LdapUser> ldapUsers;

					if (UserConnection.GetIsFeatureEnabled("GKILicensingFeatureTestMode"))
					{
						ldapUsers = new List<LDAP.LdapUser>();
						var usersSelect =
						new Select(UserConnection)
							.Column("GKIName")
						.From("GKILdapTestUsers")
						.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id)) as Select;
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = usersSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									ldapUsers.Add(new LDAP.LdapUser { Name = dr.GetValue(0).ToString(), IsActive = true });
								}
							}
						}

					}
					else
					{
						SyncLDAPResponse syncLDAPResponse = JsonConvert.DeserializeObject<SyncLDAPResponse>(
							GKILicenseLDAPHttpRequest(winInstanceUrl, GKILicensingConstantsCs.LicensingLDAP.GKIGetSyncLDAPResponseUrl, JsonConvert.SerializeObject(filter)));
						if (syncLDAPResponse.Success != true)
                        {
							throw new Exception(String.Concat("Windows instance error: ", syncLDAPResponse.ErrMsg));
						}
						ldapUsers = syncLDAPResponse.LdapUsers;
						fullReport += String.Concat(". Users acquired from AD: ", ldapUsers.Count);
						groupReport += String.Concat(". Users acquired from AD: ", ldapUsers.Count);
					}

					int usersAdded = 0;
					int usersUpdated = 0;
					List<string> ldapGroupUserNames = new List<string>();

					#region existing and new users
					foreach (var user in ldapUsers)
					{
						if (string.IsNullOrEmpty(user.Name) || !user.IsActive)
						{
							continue;
						}
						if (lastUserModifiedOn < user.ModifiedOn)
						{
							lastUserModifiedOn = user.ModifiedOn;
						}

						#region GKILicUser GKIInstanceLicUser GKIGroupAD

						var gkiLicUserSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKILicUser");
						var gkiInstanceLicUserSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIInstanceLicUser");

						#region GKILicUser
						//search for the user in GKILicUser
						var gkiLicUserQuery = new EntitySchemaQuery(gkiLicUserSchema);
						gkiLicUserQuery.UseAdminRights = false;
						gkiLicUserQuery.AddAllSchemaColumns();
						gkiLicUserQuery.Filters.Add(gkiLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIName", user.Name));
						var gkiLicUserCollection = gkiLicUserQuery.GetEntityCollection(UserConnection);
						Entity gkiLicUserEntity = gkiLicUserSchema.CreateEntity(UserConnection);
						if (gkiLicUserCollection.Count < 1)
						{
							//create if it's not found
							gkiLicUserEntity.SetDefColumnValues();
							gkiLicUserEntity.SetColumnValue("GKIName", user.Name);
							gkiLicUserEntity.SetColumnValue("GKIMSADLogin", user.Name);
							gkiLicUserEntity.SetColumnValue("GKIMSADActive", true);
							gkiLicUserEntity.Save();
							usersAdded++;

							//GKIInstanceLicUser inserting is down below in "searching user in GKIInstanceLicUser records"
						}
						else
						{
							gkiLicUserEntity = gkiLicUserCollection.First();
							gkiLicUserEntity.SetColumnValue("GKIMSADActive", true);
							gkiLicUserEntity.SetColumnValue("GKIMSADLogin", user.Name);
							usersUpdated++;
						}
						gkiLicUserEntity.Save();

						ldapUserIds.Add(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")); // ksenzov: yeah, I know that might be doubles in it, but we use it only for comparison reasons so it doesn't matter

						#endregion

						#region searching user in GKIInstanceLicUser records
						//selecting all instances that are related to this group
						var groupInstancesSelect =
							new Select(UserConnection)
								.Column("GKIInstanceId")
							.From("GKIInstanceGroupAD")
							.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id)) as Select;
						var groupInstancesList = new List<Guid>();
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = groupInstancesSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									groupInstancesList.Add(dr.GetGuid(0));
								}
							}
						}
						groupInstancesList = groupInstancesList.Distinct().ToList();
						foreach (Guid instanceId in groupInstancesList)
						{
							if (ldapInstanceUserIds.FindAll(x =>
									x.Key == instanceId &&
									x.Value == gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"))
								.Count == 0)
							{
								ldapInstanceUserIds.Add(new KeyValuePair<Guid, Guid>(instanceId, gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							}

							if (ldapGroupRelatedInstanceUserIds.FindAll(x =>
									x.Key == instanceId &&
									x.Value == gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"))
								.Count == 0)
							{
								ldapGroupRelatedInstanceUserIds.Add(new KeyValuePair<Guid, Guid>(instanceId, gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							}

							//GKIInstanceLicUser
							var gkiInstanceLicUserQuery = new EntitySchemaQuery(gkiInstanceLicUserSchema);
							gkiInstanceLicUserQuery.UseAdminRights = false;
							gkiInstanceLicUserQuery.AddAllSchemaColumns();
							gkiInstanceLicUserQuery.Filters.Add(gkiInstanceLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							gkiInstanceLicUserQuery.Filters.Add(gkiInstanceLicUserQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance",
									instanceId));
							var gkiInstanceLicUserCollection = gkiInstanceLicUserQuery.GetEntityCollection(UserConnection);
							Entity gkiInstanceLicUserEntity = gkiInstanceLicUserSchema.CreateEntity(UserConnection);
							if (gkiInstanceLicUserCollection.Count < 1)
							{
								gkiInstanceLicUserEntity.SetDefColumnValues();
								gkiInstanceLicUserEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
								gkiInstanceLicUserEntity.SetColumnValue("GKILicSyncSourceId", GKILicensingConstantsCs.GKILicSyncSource.MSAD);
								gkiInstanceLicUserEntity.SetColumnValue("GKIActive", false);
								gkiInstanceLicUserEntity.SetColumnValue("GKIMSADActive", true);
								gkiInstanceLicUserEntity.SetColumnValue("GKIInstanceId", instanceId);
							}
							else
							{
								gkiInstanceLicUserEntity = gkiInstanceLicUserCollection.First();
								gkiInstanceLicUserEntity.SetColumnValue("GKIMSADActive", true);
							}
							gkiInstanceLicUserEntity.Save();

							#endregion

							#region GKIGroupAD

							//searching for existing GKIGroupAD
							var esqGKIGroupADSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIGroupADUsers");

							var esqGKIGroupADQuery = new EntitySchemaQuery(esqGKIGroupADSchema);
							esqGKIGroupADQuery.UseAdminRights = false;
							esqGKIGroupADQuery.AddAllSchemaColumns();
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance",
									instanceId));
							esqGKIGroupADQuery.Filters.Add(esqGKIGroupADQuery.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIGroupAD",
									groupItem.Id));
							var esqGKIGroupADQueryCollection = esqGKIGroupADQuery.GetEntityCollection(UserConnection);
							Entity esqGKIGroupADEntity = esqGKIGroupADSchema.CreateEntity(UserConnection);
							if (esqGKIGroupADQueryCollection.Count < 1)
							{
								esqGKIGroupADEntity.SetDefColumnValues();
								esqGKIGroupADEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
								esqGKIGroupADEntity.SetColumnValue("GKIInstanceId", instanceId);
								esqGKIGroupADEntity.SetColumnValue("GKIGroupADId", groupItem.Id);
								esqGKIGroupADEntity.Save();
							}
							else
							{
								esqGKIGroupADEntity = esqGKIGroupADQueryCollection.First();
							}

							#endregion

							#region GKILicUserInstanceLicPackage


							var esqGKIGroupADInstanceLicenseSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKIGroupADInstanceLicense");
							var esqGKILicUserInstanceLicPackageSchema = UserConnection.EntitySchemaManager.GetInstanceByName("GKILicUserInstanceLicPackage");

							//creating a record for each of GroupAD's products if it's needed
							//if (gkiLicUserEntity.GetTypedColumnValue<bool>("GKIMSADActive")): it is always active because of the logic in GKILicUser above
							var esqGKIGroupADInstanceLicenseQuery = new EntitySchemaQuery(esqGKIGroupADInstanceLicenseSchema);
							esqGKIGroupADInstanceLicenseQuery.UseAdminRights = false;
							esqGKIGroupADInstanceLicenseQuery.AddAllSchemaColumns();
							esqGKIGroupADInstanceLicenseQuery.Filters.Add(esqGKIGroupADInstanceLicenseQuery.CreateFilterWithParameters(
								FilterComparisonType.Equal,
								"GKIGroupAD",
								groupItem.Id));
							esqGKIGroupADInstanceLicenseQuery.Filters.Add(esqGKIGroupADInstanceLicenseQuery.CreateFilterWithParameters(
								FilterComparisonType.Equal,
								"GKIInstance",
								instanceId));
							var esqGKIGroupADInstanceLicenseQueryCollection = esqGKIGroupADInstanceLicenseQuery.GetEntityCollection(UserConnection);
							foreach (Entity esqGKIGroupADInstanceLicenseEntity in esqGKIGroupADInstanceLicenseQueryCollection)
							{
								//for inactivating missing licenses later
								if (!ldapInstanceUserLicPackages.ContainsKey(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")))
								{
									ldapInstanceUserLicPackages.Add(gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"), new List<KeyValuePair<Guid, Guid>>());
								}
								var ldapInstanceUserLicPackage = ldapInstanceUserLicPackages[gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")];
								if (ldapInstanceUserLicPackage.FindAll(x =>
									x.Key == instanceId &&
									x.Value == esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")).Count == 0)
								{
									ldapInstanceUserLicPackage.Add(new KeyValuePair<Guid, Guid>(instanceId, esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")));
								}

								var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(esqGKILicUserInstanceLicPackageSchema);
								esqGKILicUserInstanceLicPackage.UseAdminRights = false;
								esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKIInstance",
									instanceId));
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKILicUser",
									gkiLicUserEntity.GetTypedColumnValue<Guid>("Id")));
								esqGKILicUserInstanceLicPackage.Filters.Add(esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(
									FilterComparisonType.Equal,
									"GKILicPackage",
									esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId")));
								var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
								if (esqGKILicUserInstanceLicPackageCollection.Count == 0)
								{
									Entity esqGKILicUserInstanceLicPackageEntity = esqGKILicUserInstanceLicPackageSchema.CreateEntity(UserConnection);
									esqGKILicUserInstanceLicPackageEntity.SetDefColumnValues();
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKILicUserId", gkiLicUserEntity.GetTypedColumnValue<Guid>("Id"));
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIInstanceId", instanceId);
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKILicPackageId", esqGKIGroupADInstanceLicenseEntity.GetTypedColumnValue<Guid>("GKILicPackageId"));
									esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
									esqGKILicUserInstanceLicPackageEntity.Save();
								}
							}

							#endregion
						}
						#endregion


					}
					#endregion

					#region group missing users
					List<Guid> groupRelatedInstancesSelect = (from kvp in ldapGroupRelatedInstanceUserIds select kvp.Key).Distinct().ToList();
					foreach (Guid instance in groupRelatedInstancesSelect)
					{
						var existingGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIGroupADUsers")
							.Where("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id))
							.And("GKIInstanceId").IsEqual(Column.Parameter(instance)) as Select;
						var existingGKIGroupADUsersList = new List<Guid>();
						using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
						{
							using (IDataReader dr = existingGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
							{
								while (dr.Read())
								{
									existingGKIGroupADUsersList.Add(dr.GetGuid(0));
								}
							}
						}

						var ldapGroupRelatedInstanceUserIdsList = (from kvp in ldapGroupRelatedInstanceUserIds where kvp.Key == instance select kvp.Value).ToList();
						IEnumerable<Guid> ldapGroupRelatedInstanceUserIdsEnum = ldapGroupRelatedInstanceUserIdsList.AsEnumerable();

						var missingGroupRelatedInstanceUsers = existingGKIGroupADUsersList.Except(ldapGroupRelatedInstanceUserIdsEnum);
						IEnumerable<QueryParameter> missingGroupUsersQueryParameter = missingGroupRelatedInstanceUsers.Select(x => new QueryParameter(x)).ToArray();
						if (missingGroupRelatedInstanceUsers.Count() > 0)
						{
							try
							{
								Delete requestRecordsDelete = new Delete(UserConnection)
									.From("GKIGroupADUsers")
									.Where("GKILicUserId")
									.In(missingGroupUsersQueryParameter)
									.And("GKIInstanceId").IsEqual(Column.Parameter(instance))
									.And("GKIGroupADId").IsEqual(Column.Parameter(groupItem.Id))
								as Delete;
								requestRecordsDelete.Execute();

								fullReport += String.Concat(". Group ", groupItem.Dn, "users deleted: ", missingGroupRelatedInstanceUsers.Count().ToString());
								groupReport += String.Concat(". Users deleted: ", missingGroupRelatedInstanceUsers.Count().ToString());
							}
							catch (Exception ex)
							{
								fullReport += String.Concat(". Group ", groupItem.Dn, "users deletion error ", ex.Message);
								groupReport += ". Users deletion error occured";
							}
						}

					}
					#endregion

					fullReport += String.Concat(". Users created: ", usersAdded.ToString(), ". Users updated: ", usersUpdated.ToString());
					groupReport += String.Concat(". Users created: ", usersAdded.ToString(), ". Users updated: ", usersUpdated.ToString());

					//write down a report into a group record
					Update updateReport = new Update(UserConnection, "GKIGroupAD")
						.Set("GKIReport", Column.Parameter(groupReport))
						.Where("Id")
						.IsEqual(Column.Parameter(groupItem.Id))
						as Update;
					int updateReportSuccess = updateReport.Execute();
				}

				#region instance missing users
				List<Guid> instancesSelect = (from kvp in ldapInstanceUserIds select kvp.Key).Distinct().ToList();
				foreach (Guid instance in instancesSelect)
				{
					var existingInstanceUsersSelect =
					new Select(UserConnection)
						.Column("Id")
						.From("GKILicUser")
						.Where("Id").In(new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIInstanceLicUser")
							.Where("GKIInstanceId").IsEqual(Column.Parameter(instance))) as Select;
					var existingInstanceUsersList = new List<Guid>();
					using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
					{
						using (IDataReader dr = existingInstanceUsersSelect.ExecuteReader(dbExecutor))
						{
							while (dr.Read())
							{
								existingInstanceUsersList.Add(dr.GetGuid(0));
							}
						}
					}

					var ldapInstanceUserIdsList = (from kvp in ldapInstanceUserIds where kvp.Key == instance select kvp.Value).ToList();
					IEnumerable<Guid> ldapInstanceUserIdsEnum = ldapInstanceUserIdsList.AsEnumerable();

					var missingInstanceUsers = existingInstanceUsersList.Except(ldapInstanceUserIdsEnum);
					IEnumerable<QueryParameter> missingInstanceUsersQueryParameter = missingInstanceUsers.Select(x => new QueryParameter(x)).ToArray();
					if (missingInstanceUsers.Count() > 0)
					{
						Update updateMissingInstanceUsers = new Update(UserConnection, "GKIInstanceLicUser")
							.Set("GKIMSADActive", Column.Parameter(false))
							.Where("GKILicUserId").In(missingInstanceUsersQueryParameter)
							.And("GKIInstanceId").IsEqual(Column.Parameter(instance))
						as Update;
						updateMissingInstanceUsers.Execute();
					}
					fullReport += String.Concat(". Instance ", instance.ToString(), "users missing: ", missingInstanceUsers.Count().ToString());
				}
				#endregion

				#region missing licenses
				foreach (var user in ldapInstanceUserLicPackages)
				{
					var instanceList = (from kvp in user.Value select kvp.Key).Distinct().ToList();
					foreach (var instanceId in instanceList)
					{
						var licenseList = (from kvp in user.Value where kvp.Key == instanceId select kvp.Value).Distinct().ToArray();
						object[] licenseParams = licenseList.Cast<object>().ToArray();
						//esq because we need to trigger the event layer
						var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(UserConnection.EntitySchemaManager, "GKILicUserInstanceLicPackage");
						esqGKILicUserInstanceLicPackage.UseAdminRights = false;
						esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser", user.Key));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance", instanceId));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIActive", true));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.NotEqual, "GKILicPackage", licenseParams));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKIInstance:GKIInstance].GKILicPackage"));

						var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
						foreach (var esqGKILicUserInstanceLicPackageEntity in esqGKILicUserInstanceLicPackageCollection)
						{
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivatedBySync", true);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivationReasonLookupId", GKILicensingConstantsCs.GKIDeactivationReasonLookup.LDAP);
							esqGKILicUserInstanceLicPackageEntity.Save();
						}
					}
				}

				#endregion

				#region missing instance users licenses
				foreach (Guid instance in allGKIInstanceGroupADUsersList.Keys)
				{
					var afterGKIGroupADUsersSelect =
						new Select(UserConnection)
							.Column("GKILicUserId")
							.From("GKIGroupADUsers")
							.Where("GKIInstanceId").IsEqual(Column.Parameter(instance)) as Select;
					var afterList = new List<Guid>();
					using (DBExecutor dbExecutor = UserConnection.EnsureDBConnection())
					{
						using (IDataReader dr = afterGKIGroupADUsersSelect.ExecuteReader(dbExecutor))
						{
							while (dr.Read())
							{
								afterList.Add(dr.GetGuid(0));
							}
						}
					}

					var beforeList = (from kvp in allGKIInstanceGroupADUsersList[instance] select kvp.Key).Distinct();
					IEnumerable<Guid> afterListEnum = afterList.AsEnumerable();

					var missingInstanceADUsers = beforeList.Except(afterListEnum);
					if (missingInstanceADUsers.Count() > 0)
					{
						object[] missingInstanceADUsersParams = missingInstanceADUsers.Cast<object>().ToArray();
						//esq because we need to trigger the event layer
						var esqGKILicUserInstanceLicPackage = new EntitySchemaQuery(UserConnection.EntitySchemaManager, "GKILicUserInstanceLicPackage");
						esqGKILicUserInstanceLicPackage.UseAdminRights = false;
						esqGKILicUserInstanceLicPackage.AddAllSchemaColumns();
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKILicUser", missingInstanceADUsersParams));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIInstance", instance));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateFilterWithParameters(FilterComparisonType.Equal, "GKIActive", true));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKIInstance:GKIInstance].Id"));
						esqGKILicUserInstanceLicPackage.Filters.Add(
							esqGKILicUserInstanceLicPackage.CreateExistsFilter("[GKIGroupADInstanceLicense:GKILicPackage:GKILicPackage].Id"));

						var esqGKILicUserInstanceLicPackageCollection = esqGKILicUserInstanceLicPackage.GetEntityCollection(UserConnection);
						foreach (var esqGKILicUserInstanceLicPackageEntity in esqGKILicUserInstanceLicPackageCollection)
						{
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIActive", false);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivatedBySync", true);
							esqGKILicUserInstanceLicPackageEntity.SetColumnValue("GKIDeactivationReasonLookupId", GKILicensingConstantsCs.GKIDeactivationReasonLookup.LDAP);
							esqGKILicUserInstanceLicPackageEntity.Save();
						}
					}
				}

				#endregion

				DateTime? maxModificationDateOfLDAPEntry = lastUserModifiedOn > DateTime.MinValue ? lastUserModifiedOn :
						default(DateTime?);
				if (maxModificationDateOfLDAPEntry.HasValue && Core.Configuration.SysSettings.Exists(UserConnection, "GKILicensingLDAPEntryMaxModifiedOn"))
				{
					Core.Configuration.SysSettings.SetDefValue(UserConnection, "GKILicensingLDAPEntryMaxModifiedOn", maxModificationDateOfLDAPEntry.Value);
				}

				var adminService = new GKILicensingAdminService(UserConnection);
				adminService.GKISlaveAndADNotInSync();
			}
            catch(Exception ex)
            {
				string errSubject = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.ExceptionRemindingSubject.Value");
				string errDescription = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.ExceptionRemindingDescription.Value");
				RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKIInstanceLDAPSelectedSyncProcess", errSubject, errDescription);
				RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKIInstanceLDAPOneSelectedSyncProcess", errSubject, errDescription);
				throw ex;
			}

			string subject = new LocalizableString(UserConnection.Workspace.ResourceStorage,
					"GKILicensingLDAPService",
					"LocalizableStrings.SuccessRemindingSubject.Value");
			string description = new LocalizableString(UserConnection.Workspace.ResourceStorage,
				"GKILicensingLDAPService",
				"LocalizableStrings.SuccessRemindingDescription.Value");
			RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKIInstanceLDAPSelectedSyncProcess", subject, description);
			RemindingServerUtilities.CreateRemindingByProcess(UserConnection, "GKIInstanceLDAPOneSelectedSyncProcess", subject, description);
			return fullReport;
		}

		/// <summary>
		/// Получение ответа от LDAP
		/// </summary>
		/// <param name="filter">фильтр выборки</param> 
		/// <returns> ответ сервиса </returns>
		[OperationContract]
		[WebInvoke(Method = "POST", RequestFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Bare,
		ResponseFormat = WebMessageFormat.Json)]
		public SyncLDAPResponse GKIGetSyncLDAPResponse(string filter)
        {
			try
			{
				List<LDAP.LdapUser> ldapUsers;

				using (var ldapUtils = new LDAP.LdapUtilities(UserConnection))
				{
					ldapUsers = ldapUtils.GetUsersByLdapFilter(filter);
				}

				SyncLDAPResponse syncLDAPResponse = new SyncLDAPResponse
				{
					Success = true,
					ErrMsg = String.Empty,
					LdapUsers = ldapUsers
				};
				return syncLDAPResponse;
			}
            catch(Exception ex)
            {
				SyncLDAPResponse syncLDAPResponse = new SyncLDAPResponse
				{
					Success = false,
					ErrMsg = ex.Message,
					LdapUsers = null
				};
				return syncLDAPResponse;
			}
		}

		/// <summary>
		/// Проверка аутентификации для экземпляра виндовой ноды
		/// </summary>
		/// <returns> ответ </returns>
		[OperationContract]
		[WebInvoke(Method = "POST", RequestFormat = WebMessageFormat.Json, BodyStyle = WebMessageBodyStyle.Wrapped,
		ResponseFormat = WebMessageFormat.Json)]
		public string GKIAuthCheck()
		{
			return "ok";
		}

		/// <summary>
		/// Получение максимальной даты изменения для фильтрации пользователей в MS AD
		/// </summary>
		/// <param name="userConnection">UserConnection</param> 
		/// <returns> максимальная дата изменения для фильтрации пользователей в MS AD </returns>
		private static DateTime GetGKIEntryMaxModifiedOn(UserConnection userConnection)
		{
			if (!Terrasoft.Core.Configuration.SysSettings.TryGetValue(userConnection, "GKILicensingLDAPEntryMaxModifiedOn",
				out var entryMaxModifiedOn))
			{
				return DateTime.MinValue;
			}
			return entryMaxModifiedOn != null
				? TimeZoneInfo.ConvertTimeToUtc((DateTime)entryMaxModifiedOn, userConnection.CurrentUser.TimeZone)
				: DateTime.MinValue;
		}

		/// <summary>
		/// Получение фильтра с применением максимальной даты изменения
		/// </summary>
		/// <param name="fromDate">максимальная дата изменения для фильтрации пользователей в MS AD</param> 
		/// <returns> фильтр </returns>
		private string GetUserFilterWithMinModifiedOnAttributeOrDate(DateTime? fromDate)
		{
			/*
			DateTime GKILdapEntryMaxModifiedOn = GetGKIEntryMaxModifiedOn(UserConnection);
			string GKILdapEntryModifiedOnAttribute = Terrasoft.Core.Configuration.SysSettings.GetValue<string>(
				UserConnection, "GKILicensingLDAPEntryModifiedOnAttribute", String.Empty);
			*/
			string GKILicensingLDAPUsersFilter = Terrasoft.Core.Configuration.SysSettings.GetValue<string>(
				UserConnection, "GKILicensingLDAPUsersFilter", String.Empty);
			/*
			if (GKILdapEntryMaxModifiedOn == DateTime.MinValue || string.IsNullOrEmpty(GKILdapEntryModifiedOnAttribute))
			{
				return GKILicensingLDAPUsersFilter;
			}
			*/
			return GKILicensingLDAPUsersFilter;
			/*
			DateTime fromDateValue = fromDate ?? DateTime.MaxValue;
			DateTime syncFromDate = fromDateValue < _ldapEntryMaxModifiedOn ? fromDateValue : _ldapEntryMaxModifiedOn;
			string lastSyncDateInLdapFormat = ConvertToLdapFormat(syncFromDate);
			var modifiedOnAttributeFilter = string.Format("({0}>={1})", _ldapEntryModifiedOnAttribute,
				lastSyncDateInLdapFormat);
			return "(&" + _ldapUsersFilter + modifiedOnAttributeFilter + ")";
			*/
		}

		/// <summary>
		/// Замена специальных символов чувствительных для LDAP
		/// </summary>
		/// <param name="filterString">фильтр</param> 
		/// <returns> фильтр </returns>
		private string ReplaceSpecialCharacters(string filterString)
		{
			return filterString.Replace("*", "\\2A")
				.Replace("(", "\\28")
				.Replace(")", "\\29")
				.Replace("\\", "\\5C")
				.Replace("Nul", "\\00");
		}

		/// <summary>
		/// Получение фильтра пользователя в группе
		/// </summary>
		/// <param name="filter">фильтр</param> 
		/// <param name="group">группа</param> 
		/// <returns> фильтр </returns>
		private string GKIGetUserInGroupFilterString(string filter, GKILicensingLdapGroup group)
		{
			string groupDn = ReplaceSpecialCharacters(group.Dn);
			return filter.Replace(GKILicensingConstantsCs.LicensingLDAP.gkiLdapGroupMacroName, groupDn);
		}

		/// <summary>
		/// Авторизация в экземпляре виндовой ноды
		/// </summary>
		/// <param name="baseUrl">url экземпляра</param> 
		/// <returns> ответ </returns>
		private string GKIAuthorize(string baseUrl)
		{
			string authUrl = string.Concat(baseUrl, GKILicensingConstantsCs.LicensingServices.authServicePath);
			string authLogin = (string)Terrasoft.Core.Configuration.SysSettings.GetValue(UserConnection, "GKILicensingWinInstanceLogin");
			string authPassword = (string)Terrasoft.Core.Configuration.SysSettings.GetValue(UserConnection, "GKILicensingWinInstancePassword");

			string authMessage = String.Format(GKILicensingConstantsCs.LicensingServices.authTemplate, authLogin, authPassword);

			if (authMessage == String.Empty)
			{
				return "Error: no authentification credentials in GKILicensingWinInstanceCredentials";
			}

			if (AuthCookies == null)
			{
				AuthCookies = new CookieContainer();
			}

			if (AuthCookies.GetCookies(new Uri(baseUrl)).Count > 0)
			{
				try
				{
					HttpWebRequest httprequest = (HttpWebRequest)WebRequest.Create(String.Concat(baseUrl, GKILicensingConstantsCs.LicensingLDAP.GKIAuthCheckServiceUrl));
					httprequest.Method = "POST";
					httprequest.Accept = @"application/json";
					httprequest.ContentLength = 0;
					httprequest.ContentType = @"application/json";
					httprequest.CookieContainer = AuthCookies;
					var crsfcookie = httprequest.CookieContainer.GetCookies(new Uri(baseUrl))[GKILicensingConstantsCs.LicensingServices.crsfName];
					if (crsfcookie != null)
					{
						httprequest.Headers.Add(GKILicensingConstantsCs.LicensingServices.crsfName, crsfcookie.Value);
					}

					using (HttpWebResponse response = (HttpWebResponse)httprequest.GetResponse())
					{
						using (var streamReader = new StreamReader(response.GetResponseStream()))
						{
							var msgResponseText = streamReader.ReadToEnd();
						}
					}
				}
				catch (WebException ex)
				{
					using (var stream = ex.Response.GetResponseStream())
					using (var reader = new StreamReader(stream))
					{
						string exceptionMsg = reader.ReadToEnd();
						if (exceptionMsg.Contains("401"))
						{
							string authResult = GKIAuthorizeForced(baseUrl);
							if (authResult != String.Empty)
							{
								return authResult;
							}
							else
							{
								return String.Empty;
							}
						}
						else
						{
							return exceptionMsg;
						}
					}
				}
				catch (Exception ex)
				{
					return ex.Message;
				}
				return String.Empty;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(authMessage);

			HttpWebRequest authrequest = (HttpWebRequest)WebRequest.Create(authUrl);
			authrequest.Method = "POST";
			authrequest.Accept = @"application/json";
			authrequest.ContentLength = bytes.Length;
			authrequest.ContentType = @"application/json";
			authrequest.CookieContainer = AuthCookies;

			using (Stream streamresponse = authrequest.GetRequestStream())
			{
				streamresponse.Write(bytes, 0, bytes.Length);
			}
			string responseText;
			ResponseStatus status = null;

			using (HttpWebResponse response = (HttpWebResponse)authrequest.GetResponse())
			{
				string authCookeValue = response.Cookies[GKILicensingConstantsCs.LicensingServices.authName].Value;
				string crsfCookeValue = response.Cookies[GKILicensingConstantsCs.LicensingServices.crsfName].Value;
				AuthCookies.Add(new Uri(baseUrl), new Cookie(GKILicensingConstantsCs.LicensingServices.authName, authCookeValue));
				AuthCookies.Add(new Uri(baseUrl), new Cookie(GKILicensingConstantsCs.LicensingServices.crsfName, crsfCookeValue));
				using (var streamReader = new StreamReader(response.GetResponseStream()))
				{
					responseText = streamReader.ReadToEnd();
					status = JsonConvert.DeserializeObject<ResponseStatus>(responseText);
				}
			}

			if (status == null || status.Code != 0)
			{
				AuthCookies = new CookieContainer();
				return responseText;
			}
			else
			{
				return String.Empty;
			}
		}

		/// <summary>
		/// Повторная авторизация в экземпляре виндовой ноды
		/// </summary>
		/// <param name="baseUrl">url экземпляра</param> 
		/// <returns> ответ </returns>
		private string GKIAuthorizeForced(string baseUrl)
		{
			string authUrl = string.Concat(baseUrl, GKILicensingConstantsCs.LicensingServices.authServicePath);
			string authLogin = (string)Terrasoft.Core.Configuration.SysSettings.GetValue(UserConnection, "GKILicensingWinInstanceLogin");
			string authPassword = (string)Terrasoft.Core.Configuration.SysSettings.GetValue(UserConnection, "GKILicensingWinInstancePassword");

			string authMessage = String.Format(GKILicensingConstantsCs.LicensingServices.authTemplate, authLogin, authPassword);

			if (authMessage == String.Empty)
			{
				return "Error: no authentification credentials in GKILicensingWinInstanceCredentials";
			}

			AuthCookies = new CookieContainer();

			byte[] bytes = Encoding.UTF8.GetBytes(authMessage);

			HttpWebRequest authrequest = (HttpWebRequest)WebRequest.Create(authUrl);
			authrequest.Method = "POST";
			authrequest.Accept = @"application/json";
			authrequest.ContentLength = bytes.Length;
			authrequest.ContentType = @"application/json";
			authrequest.CookieContainer = AuthCookies;

			using (Stream streamresponse = authrequest.GetRequestStream())
			{
				streamresponse.Write(bytes, 0, bytes.Length);
			}
			string responseText;
			ResponseStatus status = null;

			using (HttpWebResponse response = (HttpWebResponse)authrequest.GetResponse())
			{
				string authCookeValue = response.Cookies[GKILicensingConstantsCs.LicensingServices.authName].Value;
				string crsfCookeValue = response.Cookies[GKILicensingConstantsCs.LicensingServices.crsfName].Value;
				AuthCookies.Add(new Uri(baseUrl), new Cookie(GKILicensingConstantsCs.LicensingServices.authName, authCookeValue));
				AuthCookies.Add(new Uri(baseUrl), new Cookie(GKILicensingConstantsCs.LicensingServices.crsfName, crsfCookeValue));
				using (var streamReader = new StreamReader(response.GetResponseStream()))
				{
					responseText = streamReader.ReadToEnd();
					status = JsonConvert.DeserializeObject<ResponseStatus>(responseText);
				}
			}

			if (status == null || status.Code != 0)
			{
				AuthCookies = new CookieContainer();
				return responseText;
			}
			else
			{
				return String.Empty;
			}
		}

		/// <summary>
		/// Запрос в экземпляр виндовой ноды
		/// </summary>
		/// <param name="baseUrl">url экземпляра</param> 
		/// <param name="serviceUrl">url сервиса</param> 
		/// <param name="message">сообщение</param> 
		/// <returns> ответ </returns>
		private string GKILicenseLDAPHttpRequest(string baseUrl, string serviceUrl, string message)
		{
			string authResult = GKIAuthorize(baseUrl);
			if (authResult != String.Empty)
			{
				throw new Exception(authResult);
			}
			byte[] msgBytes = Encoding.UTF8.GetBytes(message);

			HttpWebRequest httprequest = (HttpWebRequest)WebRequest.Create(String.Concat(baseUrl, serviceUrl));
			httprequest.Method = "POST";
			httprequest.Accept = @"application/json";
			httprequest.ContentLength = msgBytes.Length;
			httprequest.ContentType = @"application/json";
			httprequest.CookieContainer = AuthCookies;
			var crsfcookie = httprequest.CookieContainer.GetCookies(new Uri(baseUrl))[GKILicensingConstantsCs.LicensingServices.crsfName];
			if (crsfcookie != null)
			{
				httprequest.Headers.Add(GKILicensingConstantsCs.LicensingServices.crsfName, crsfcookie.Value);
			}
			using (Stream streamresponse = httprequest.GetRequestStream())
			{
				streamresponse.Write(msgBytes, 0, msgBytes.Length);
			}
			using (HttpWebResponse response = (HttpWebResponse)httprequest.GetResponse())
			{
				using (var streamReader = new StreamReader(response.GetResponseStream()))
				{
					var msgResponseText = streamReader.ReadToEnd();
					return msgResponseText;
				}
			}
		}
		public class ResponseStatus
		{
			public int Code { get; set; }
			public string Message { get; set; }
			public object Exception { get; set; }
			public object PasswordChangeUrl { get; set; }
			public object RedirectUrl { get; set; }
		}
		public class SyncLDAPResponse
        {
			public bool Success { get; set; }
			public string ErrMsg { get; set; }
			public List<LDAP.LdapUser> LdapUsers { get; set; }
		}

		public struct GKILicensingLdapGroup
		{

			#region Fields: Public

			public Guid Id;
			public string Dn;
			public DateTime ModifiedOn;

			#endregion

			#region Constructors: Public

			public GKILicensingLdapGroup(Guid id, string dn)
			{
				Id = id;
				Dn = dn;
				ModifiedOn = DateTime.MinValue;
			}

			#endregion

		}

		public struct GKILicensingLdapUser
		{

			#region Fields: Public

			public string Id;
			public string Name;
			public string FullName;
			public string Company;
			public string Email;
			public string Phone;
			public string JobTitle;
			public bool IsActive;
			public string Dn;
			public DateTime ModifiedOn;

			#endregion

		}

	}

}