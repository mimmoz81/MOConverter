﻿using Converter.Interface;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Diagnostics;
using System.Linq;

namespace Converter.Extension
{
    public static partial class TableExtension
    {


        public static bool SwitchToMo(
                                      this Table self, 
                                      Database InMemDatabase, 
                                      Database Traditional, 
                                      Configuration.Configuration cnf, 
                                      Options.Options o, 
                                      ref string error, 
                                      ILog logger,
                                      SQLServerMoFeatures enumFeatures)
        {
            bool retValue = false;
            string schemaName = self.Schema;
            string dataBaseName = self.Parent.Name;

            if (InMemDatabase.Schemas.Contains(schemaName) == false)
            {
                Schema hr = new Schema(InMemDatabase, schemaName);
                InMemDatabase.Schemas.Add(hr);
                InMemDatabase.Schemas[schemaName].Create();
            }

            if (InMemDatabase.Tables.Contains(self.Name, schemaName))
            {
                logger.Log("\t" + "Already exists", self.FName());
                return true;
            }


            bool hasIdentities = false;


            Table newTable = new Table(InMemDatabase, self.Name, schemaName);

            // default true
            newTable.IsMemoryOptimized = true;
            // default schema and data
            newTable.Durability = DurabilityType.SchemaAndData;


            // Add columns
            Column newColumn = null;
            foreach (Column c in self.Columns)
            {
                newColumn = new Column(newTable, c.Name);

                newColumn.CopyPropertiesFrom(c);

                SupportUnsupported(newColumn, c, Traditional, logger, ref error, ref hasIdentities,true);

                newTable.Columns.Add(newColumn);

            }


            //Add indexes
            bool hasPrimaryKey = false;
            foreach (Index i in self.Indexes)
            {
                if (i.IndexKeyType == IndexKeyType.DriPrimaryKey)
                {
                    hasPrimaryKey = true;
                    Index idx = new Index(newTable, i.Name);
                    if (o.UseHashIndexes == Options.Options.IndexDecision.Hash)
                    {
                        idx.IndexType = IndexType.NonClusteredHashIndex;
                        idx.BucketCount = self.RowCount == 0 ? 64 : (int)self.RowCount *2;
                    }
                    else if ( o.UseHashIndexes == Options.Options.IndexDecision.Range)
                    {
                        idx.IndexType = IndexType.NonClusteredIndex;
                    }
                    else
                    {
                        if (self.ExtendedProperties[cnf.EPName] != null)
                        {
                            string value = self.ExtendedProperties[cnf.EPName].Value.ToString().ToUpper();
                            if (value == "HASH")
                            {
                                idx.IndexType = IndexType.NonClusteredHashIndex;
                                idx.BucketCount = self.RowCount == 0 ? 64 : (int)self.RowCount;
                            }
                            else
                            {
                                idx.IndexType = IndexType.NonClusteredIndex;
                            }
                        }
                        else
                        {
                            idx.IndexType = IndexType.NonClusteredIndex;
                        }

                       
                    }
                    idx.IndexKeyType = IndexKeyType.DriPrimaryKey;
                    foreach (IndexedColumn ic in i.IndexedColumns)
                    {
                        idx.IndexedColumns.Add(new IndexedColumn(idx, ic.Name));
                    }
                    newTable.Indexes.Add(idx);
                }
            }

            int noIndexes = hasPrimaryKey ? 1 : 0;
            foreach (Index i in self.Indexes)
            {
                if (i.IndexKeyType == IndexKeyType.DriPrimaryKey)
                {
                    continue;
                }
                else if (i.IndexType == IndexType.NonClusteredIndex)
                {

                    // Limit the total number of indexes to 8 for SQLServer2016
                    if (enumFeatures == SQLServerMoFeatures.SQLServer2016 && noIndexes == 8)
                    {
                        logger.LogWarErr("Error:Create index failed", "Could not create in-memory index " + i.Name + " because it exceeds the maximum of 8 allowed per table or view.");
                        continue;
                    }

                    Index idx = new Index(newTable, i.Name);
                    idx.IndexType = IndexType.NonClusteredIndex;
                    idx.IndexKeyType = IndexKeyType.None;
                    bool hasColumns = false;
                    foreach (IndexedColumn ic in i.IndexedColumns)
                    {
                        if (ic.IsIncluded)
                            continue;
                        // nvarchar(max) is not allowed
                        if (newTable.Columns[ic.Name].DataType.MaximumLength == -1)
                        {
                            logger.LogWarErr("Warning:Create index", "Could not include " + ic.Name + " in index " + idx.Name + " The column has nvarchar(max) type which is not allowed!");
                            continue;
                        }
                        idx.IndexedColumns.Add(new IndexedColumn(idx, ic.Name));
                        hasColumns = true;
                    }
                    if (hasColumns)
                    {
                        newTable.Indexes.Add(idx);
                        noIndexes++;

                    }
                }
            }


            if (hasPrimaryKey == false)
            {
                error = "Error:Table :" + self.FName() + " has no primary key";
                //logger.LogWarErr(error, self.FName());
                return false;
            }

            //if (InMemDatabase.Tables.Contains(newTable.Name, schemaName))
            //    InMemDatabase.Tables[newTable.Name, schemaName].Drop();

            // Add checks
            foreach (Check ch in self.Checks)
            {
                Check newch = new Check(newTable, ch.Name);
                newch.CopyPropertiesFrom(ch);
                if (newch.Text.ToLower().Contains("upper") || newch.Text.ToLower().Contains("like") || newch.Text.ToLower().Contains("charindex"))
                {
                    logger.LogWarErr("Warning " + newch.Name, " can not apply constraint on table " + self.FName() + " because it contains forbidden functions ");
                    continue;
                }
                newTable.Checks.Add(newch);
            }

            // Skip triggers
            foreach (Trigger tr in self.Triggers)
            {
                logger.LogWarErr("Warning " + tr.Name, " can not create trigger on table " + self.FName() + " Please, create trigger manually! ");
                continue;
            }

            try
            {
                logger.Log("Create table ", newTable.FName());
                newTable.Create();
            }
            catch (Exception ex)
            {
                if (Debugger.IsAttached)
                    Debugger.Break();
               
                error = String.Join(Environment.NewLine + "\t", ex.CollectThemAll(ex1 => ex1.InnerException)
                    .Select(ex1 => ex1.Message));
                return false;
            }


            // if copy data is checked
            if (o.CopyData)
            {
                if (InMemDatabase.Tables.Contains(cnf.helperTableName, cnf.helperSchema))
                    InMemDatabase.Tables[cnf.helperTableName, cnf.helperSchema].Drop();

                try
                {
                    logger.Log("Insert data ", newTable.FName());
                    //Insert into 
                    Traditional.ExecuteNonQuery(self.insertIntoStm(InMemDatabase.Name, cnf.fullName));
                    //Insert statement
                    Table test = InMemDatabase.Tables[cnf.helperTableName, cnf.helperSchema];
                    InMemDatabase.ExecuteNonQuery(newTable.fullInsertStm(test.selectStm(), hasIdentities, cnf.fullName));
                    retValue = true;
                    logger.Log("OK ", newTable.FName());
                    //
                }
                catch (Exception ex)
                {
                    if (InMemDatabase.Tables.Contains(newTable.Name, schemaName))
                        InMemDatabase.Tables[newTable.Name, schemaName].Drop();
                    if (Debugger.IsAttached)
                        Debugger.Break();

                    logger.Log("Error", self.FName());
                

                    error = String.Join(Environment.NewLine + "\t", ex.CollectThemAll(ex1 => ex1.InnerException)
                        .Select(ex1 => ex1.Message));

                    return false;
                }
            }


            newTable = null;

            return retValue;
        }

        public static void SupportUnsupported(Column newColumn, Column c, Database Traditional, ILog logger, ref string error, ref bool hasIdentities, bool isTable)
        {
            if (c.DataType.SqlDataType == SqlDataType.UserDefinedDataType)
            {
                UserDefinedDataType ud = Traditional.UserDefinedDataTypes[c.DataType.Name, c.DataType.Schema];
                try
                {
                    newColumn.DataType = new DataType();
                    newColumn.DataType.SqlDataType = Uddt.Type.DetermineSqlDataType(ud.SystemType, ref error);
                    newColumn.DataType.MaximumLength = c.DataType.MaximumLength;
                    newColumn.DataType.NumericScale = c.DataType.NumericScale;
                    newColumn.DataType.NumericPrecision = c.DataType.NumericPrecision;
                    newColumn.Nullable = c.Nullable;
                    newColumn.Default = c.Default;
                    logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                      "Convertion datatype : " + ud.Name + " to " + newColumn.DataType.SqlDataType.ToString());
                }
                catch (Exception ex)
                {
                    if (Debugger.IsAttached)
                        Debugger.Break();


                    error = String.Join(Environment.NewLine + "\t", ex.CollectThemAll(ex1 => ex1.InnerException)
                        .Select(ex1 => ex1.Message));
                    logger.LogWarErr("COLUMN:Error", error);
                    return;
                }
            }
            // support for CLR types
            else if (c.DataType.SqlDataType == SqlDataType.HierarchyId || c.DataType.SqlDataType == SqlDataType.Geography || c.DataType.SqlDataType == SqlDataType.Geometry)
            {
                newColumn.DataType.SqlDataType = SqlDataType.NVarChar;
                if (c.DataType.SqlDataType == SqlDataType.HierarchyId)
                    newColumn.DataType.MaximumLength = 1000;
                else
                    newColumn.DataType.MaximumLength = -1;

                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                  "Convertion CLR datatype to " + newColumn.DataType.SqlDataType.ToString());
            }
            // support for XML type
            else if (c.DataType.SqlDataType == SqlDataType.Xml)
            {
                newColumn.DataType.SqlDataType = SqlDataType.NVarChar;
                newColumn.DataType.MaximumLength = -1;
                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                 "Convertion XML datatype to " + newColumn.DataType.SqlDataType.ToString());
            }
            else if (c.DataType.SqlDataType == SqlDataType.Variant || c.DataType.SqlDataType == SqlDataType.Text || c.DataType.SqlDataType == SqlDataType.NText)
            {
                newColumn.DataType.SqlDataType = SqlDataType.NVarChar;
                newColumn.DataType.MaximumLength = -1;
                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                 "Convertion " + c.DataType.SqlDataType + " TO " + newColumn.DataType.SqlDataType.ToString());
            }
            else if (c.DataType.SqlDataType == SqlDataType.Image)
            {
                newColumn.DataType.SqlDataType = SqlDataType.VarBinaryMax;
                newColumn.DataType.MaximumLength = -1;
                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                 "Convertion " + c.DataType.SqlDataType + " TO " + newColumn.DataType.SqlDataType.ToString());
            }
            else if (c.DataType.SqlDataType == SqlDataType.DateTimeOffset)
            {
                newColumn.DataType.SqlDataType = SqlDataType.DateTime2;
                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                 "Convertion " + c.DataType.SqlDataType + " TO " + newColumn.DataType.SqlDataType.ToString());
            }
            //else
            //{
            //newColumn.DataType = c.DataType;
            //}

            if (c.Computed)
            {
                newColumn.Computed = false;
                newColumn.ComputedText = String.Empty;
                //newColumn.IsPersisted = c.IsPersisted;
                logger.LogWarErr("Warning " + (isTable == true ? c.FName() : c.UDTName()),
                                 "Can not apply computed column " + c.ComputedText);
            }

            //newColumn.Collation = c.Collation;
            //newColumn.Identity = c.Identity;
            if (c.Identity)
            {

                hasIdentities = true;
                // has to be 1
                if (c.IdentityIncrement != 1 || c.IdentitySeed != 1)
                    logger.LogWarErr("Warning " + (isTable == true ? newColumn.FName() : newColumn.UDTName()),
                                    " setting identity seed and identity increment to 1 ");
                newColumn.IdentityIncrement = 1;
                newColumn.IdentitySeed = 1;


            }
            return;

        }



    }
}