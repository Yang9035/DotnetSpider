﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using DotnetSpider.Core;
using Newtonsoft.Json.Linq;
using DotnetSpider.Extension.ORM;
using DotnetSpider.Core.Common;
using DotnetSpider.Extension.Model;
using Newtonsoft.Json;
using NLog;

namespace DotnetSpider.Extension.Pipeline
{
	public abstract class BaseEntityDbPipeline : BaseEntityPipeline
	{
		public string ConnectString { get; set; }
		public PipelineMode Mode { get; set; } = PipelineMode.Insert;
		[JsonIgnore]
		public IUpdateConnectString UpdateConnectString { get; set; }
		protected abstract DbConnection CreateConnection();
		protected abstract string GetInsertSql();
		protected abstract string GetUpdateSql();
		protected abstract string GetCreateTableSql();
		protected abstract string GetCreateSchemaSql();
		protected abstract DbParameter CreateDbParameter();

		protected List<List<string>> Indexs { get; set; } = new List<List<string>>();
		protected List<List<string>> Uniques { get; set; } = new List<List<string>>();
		protected List<Field> Primary { get; set; } = new List<Field>();
		protected string AutoIncrement { get; set; }
		protected Schema Schema { get; set; }
		protected List<Field> Columns { get; set; } = new List<Field>();
		protected List<Field> UpdateColumns { get; set; } = new List<Field>();

		protected abstract string ConvertToDbType(string datatype);

		protected BaseEntityDbPipeline()
		{
		}

		protected BaseEntityDbPipeline(string connectString, PipelineMode mode = PipelineMode.Insert)
		{
			Mode = mode;
			ConnectString = connectString;
		}

		public Schema GetSchema()
		{
			return Schema;
		}

		public override void InitiEntity(EntityMetadata metadata)
		{
			if (metadata.Schema == null)
			{
				IsEnabled = false;
				return;
			}
			Schema = GenerateSchema(metadata.Schema);
			foreach (var f in metadata.Entity.Fields)
			{
				if (!string.IsNullOrEmpty(((Field)f).DataType))
				{
					Columns.Add((Field)f);
				}
			}
			var primary = metadata.Primary;
			if (primary != null)
			{
				foreach (var p in primary)
				{
					var col = Columns.FirstOrDefault(c => c.Name == p);
					if (col == null)
					{
						throw new SpiderException("Columns set as primary is not a property of your entity.");
					}
					else
					{
						Primary.Add(col);
					}
				}
			}

			if (Mode == PipelineMode.Update)
			{
				if (Primary == null || Primary.Count == 0)
				{
					throw new SpiderException("Set Primary in the Indexex attribute.");
				}

				if (metadata.Updates != null && metadata.Updates.Length > 0)
				{
					foreach (var column in metadata.Updates)
					{
						var col = Columns.FirstOrDefault(c => c.Name == column);
						if (col == null)
						{
							throw new SpiderException("Columns set as update is not a property of your entity.");
						}
						else
						{
							UpdateColumns.Add(col);
						}
					}

					UpdateColumns.RemoveAll(c => Primary.Contains(c));

					if (UpdateColumns.Count == 0)
					{
						throw new SpiderException("Can't update primary key.");
					}
				}
				else
				{
					UpdateColumns = Columns;
					UpdateColumns.RemoveAll(c => Primary.Contains(c));

					if (UpdateColumns.Count == 0)
					{
						throw new SpiderException("Can't update primary key.");
					}
				}
			}

			AutoIncrement = metadata.AutoIncrement;

			if (metadata.Indexes != null)
			{
				foreach (var index in metadata.Indexes)
				{
					List<string> tmpIndex = new List<string>();
					foreach (var i in index)
					{
						var col = Columns.FirstOrDefault(c => c.Name == i);
						if (col == null)
						{
							throw new SpiderException("Columns set as index is not a property of your entity.");
						}
						else
						{
							tmpIndex.Add(col.Name);
						}
					}
					if (tmpIndex.Count != 0)
					{
						Indexs.Add(tmpIndex);
					}
				}
			}
			if (metadata.Uniques != null)
			{
				foreach (var unique in metadata.Uniques)
				{
					List<string> tmpUnique = new List<string>();
					foreach (var i in unique)
					{
						var col = Columns.FirstOrDefault(c => c.Name == i);
						if (col == null)
						{
							throw new SpiderException("Columns set as unique is not a property of your entity.");
						}
						else
						{
							tmpUnique.Add(col.Name);
						}
					}
					if (tmpUnique.Count != 0)
					{
						Uniques.Add(tmpUnique);
					}
				}
			}
		}

		public override void InitPipeline(ISpider spider)
		{
			if (!IsEnabled)
			{
				return;
			}

			if (string.IsNullOrEmpty(ConnectString))
			{
				if (UpdateConnectString == null)
				{
					throw new SpiderException("Can't find ConnectString or IUpdateConnectString.");
				}
				else
				{
					for (int i = 0; i < 5; ++i)
					{
						try
						{
							ConnectString = UpdateConnectString.GetNew();
							break;
						}
						catch (Exception e)
						{
							Logger.SaveLog(LogInfo.Create("Update ConnectString failed.", Logger.Name, spider, LogLevel.Error, e));
							Thread.Sleep(1000);
						}
					}

					if (string.IsNullOrEmpty(ConnectString))
					{
						throw new SpiderException("Can't updadate ConnectString via IUpdateConnectString.");
					}
				}
			}

			base.InitPipeline(spider);

			if (Mode == PipelineMode.Update)
			{
				return;
			}

			NetworkCenter.Current.Execute("db-init", () =>
			{
				using (DbConnection conn = CreateConnection())
				{
					conn.Open();
					var command = conn.CreateCommand();
					command.CommandText = GetCreateSchemaSql();
					command.CommandType = CommandType.Text;
					command.ExecuteNonQuery();

					command.CommandText = GetCreateTableSql();
					command.CommandType = CommandType.Text;
					command.ExecuteNonQuery();
					conn.Close();
				}
			});
		}

		public override void Process(List<JObject> datas)
		{
			if (!IsEnabled)
			{
				return;
			}
			NetworkCenter.Current.Execute("pp-", () =>
			{
				switch (Mode)
				{
					case PipelineMode.Insert:
						{
							int tryCount = 0;
							while (true)
							{
								try
								{
									using (var conn = CreateConnection())
									{
										var cmd = conn.CreateCommand();
										cmd.CommandText = GetInsertSql();
										cmd.CommandType = CommandType.Text;
										conn.Open();

										foreach (var data in datas)
										{
											cmd.Parameters.Clear();

											List<DbParameter> parameters = new List<DbParameter>();
											foreach (var column in Columns)
											{
												var parameter = CreateDbParameter();
												parameter.ParameterName = $"@{column.Name}";
												parameter.Value = data.SelectToken($"{column.Name}")?.Value<string>();
												parameter.DbType = Convert(column.DataType);
												parameters.Add(parameter);
											}

											cmd.Parameters.AddRange(parameters.ToArray());
											cmd.ExecuteNonQuery();
										}

										conn.Close();
									}
									break;
								}
								catch (Exception e)
								{
									// mysql authentication error
									if (e.Message.ToLower().StartsWith("authentication to host"))
									{
										Thread.Sleep(1000);
										if (tryCount > 5)
										{
											throw;
										}
										if (UpdateConnectString != null)
										{
											tryCount++;
											ConnectString = UpdateConnectString.GetNew();
										}
										else
										{
											throw;
										}
									}
									else
									{
										throw;
									}
								}
							}
							break;
						}
					case PipelineMode.Update:
						{
							int tryCount = 0;
							while (true)
							{
								try
								{
									using (var conn = CreateConnection())
									{
										var cmd = conn.CreateCommand();
										cmd.CommandText = GetUpdateSql();
										cmd.CommandType = CommandType.Text;
										conn.Open();

										foreach (var data in datas)
										{
											cmd.Parameters.Clear();

											List<DbParameter> parameters = new List<DbParameter>();
											foreach (var column in UpdateColumns)
											{
												var parameter = CreateDbParameter();
												parameter.ParameterName = $"@{column.Name}";
												parameter.Value = data.SelectToken($"{column.Name}")?.Value<string>();
												parameter.DbType = Convert(column.DataType);
												parameters.Add(parameter);
											}

											foreach (var column in Primary)
											{
												var parameter = CreateDbParameter();
												parameter.ParameterName = $"@{column.Name}";
												parameter.Value = data.SelectToken($"{column.Name}")?.Value<string>();
												parameter.DbType = Convert(column.DataType);
												parameters.Add(parameter);
											}

											cmd.Parameters.AddRange(parameters.ToArray());
											cmd.ExecuteNonQuery();
										}

										conn.Close();
									}
									break;
								}
								catch (Exception e)
								{
									// mysql authentication error
									if (e.Message.ToLower().StartsWith("authentication to host"))
									{
										Thread.Sleep(1000);
										if (tryCount > 5)
										{
											throw;
										}
										if (UpdateConnectString != null)
										{
											tryCount++;
											ConnectString = UpdateConnectString.GetNew();
										}
										else
										{
											throw;
										}
									}
									else
									{
										throw;
									}
								}
							}
							break;
						}
				}
			});
		}

		private Schema GenerateSchema(Schema schema)
		{
			switch (schema.Suffix)
			{
				case TableSuffix.FirstDayOfThisMonth:
					{
						schema.TableName += "_" + DateTimeUtils.FirstDayofThisMonth.ToString("yyyy_MM_dd");
						break;
					}
				case TableSuffix.Monday:
					{
						schema.TableName += "_" + DateTimeUtils.FirstDayofThisWeek.ToString("yyyy_MM_dd");
						break;
					}
				case TableSuffix.Today:
					{
						schema.TableName += "_" + DateTime.Now.ToString("yyyy_MM_dd");
						break;
					}
			}
			return schema;
		}

		private DbType Convert(string datatype)
		{
			if (string.IsNullOrEmpty(datatype))
			{
				throw new SpiderException("TYPE can not be null");
			}

			if (datatype.StartsWith("STRING,") || "TEXT" == datatype)
			{
				return DbType.String;
			}
			if ("BOOL" == datatype)
			{
				return DbType.Boolean;
			}

			if ("DATE" == datatype || "TIME" == datatype)
			{
				return DbType.DateTime;
			}

			throw new SpiderException("Unsport datatype: " + datatype);
		}
	}
}

