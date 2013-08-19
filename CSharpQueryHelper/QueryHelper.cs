﻿using System;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Linq;

namespace CSharpQueryHelper
{
    public interface IQueryHelper
    {
        void NonQueryToDBWithTransaction(IEnumerable<NonQueryWithParameters> queries);
        void NonQueryToDB(NonQueryWithParameters query);
        T ReadScalerDataFromDB<T>(string sql);
        T ReadScalerDataFromDB<T>(SQLQueryWithParameters query);
        void ReadDataFromDB(string sql, Func<DbDataReader, bool> processRow);
        void ReadDataFromDB(SQLQueryWithParameters query);
    }

    public class QueryHelper : IQueryHelper
    {
        private readonly string ConnectionString;
        private readonly string DataProvider;
        private readonly DbProviderFactory DbFactory;

        public QueryHelper(string connectionString, string dataProvider)
        {
            this.ConnectionString = connectionString;
            this.DataProvider = dataProvider;
            this.DbFactory = DbProviderFactories.GetFactory(DataProvider);
        }

        public QueryHelper(string connectionName)
        {
            if (ConfigurationManager.ConnectionStrings[connectionName] == null)
            {
                throw new ArgumentException("'"+ connectionName + "' is not a valid connection string name. Check your configuration file to make sure it exists.", "connectionName");
            }
            this.ConnectionString = ConfigurationManager.ConnectionStrings[connectionName].ConnectionString;
            this.DataProvider = ConfigurationManager.ConnectionStrings[connectionName].ProviderName;
            this.DbFactory = DbProviderFactories.GetFactory(DataProvider);
        }

        public void NonQueryToDBWithTransaction(IEnumerable<NonQueryWithParameters> queries)
        {
            DbTransaction transaction = null;
            try
            {
                using (var conn = CreateConnection())
                {
                    conn.Open();
                    transaction = conn.BeginTransaction();
                    foreach (var query in queries.OrderBy(q=>q.Order))
                    {
                        var command = CreateCommand(query.SQL, conn, transaction);
                        AddParameters(command, query);
                        query.RowCount = command.ExecuteNonQuery();
                        ProcessIdentityColumn(query, conn, transaction);
                    }
                    transaction.Commit();
                    transaction = null;
                    conn.Close();
                }
            }
            catch (Exception)
            {
                try
                {
                    if (transaction != null)
                    {
                        transaction.Rollback();
                    }
                }
                catch { }
                throw;
            }
        }

        public void NonQueryToDB(NonQueryWithParameters query)
        {
            query.RowCount = 0;
            using (var conn = CreateConnection())
            {
                conn.Open();
                var command = CreateCommand(query.SQL, conn);
                AddParameters(command, query);
                query.RowCount = command.ExecuteNonQuery();
                ProcessIdentityColumn(query, conn);
                conn.Close();
            }
        }

        public T ReadScalerDataFromDB<T>(string sql)
        {
            return ReadScalerDataFromDB<T>(new SQLQueryWithParameters(sql, null));
        }
        
        public T ReadScalerDataFromDB<T>(SQLQueryWithParameters query)
        {
            var conn = CreateConnection();
            using (conn)
            {
                conn.Open();
                var command = CreateCommand(query.SQL, conn);
                AddParameters(command, query);
                var result = command.ExecuteScalar();
                query.RowCount = 1;
                if (result != DBNull.Value)
                {
                    if (result is T)
                    {
                        return (T)result;
                    }
                    else if (typeof(T) == typeof(string))
                    {
                        object stringResult = (object)result.ToString();
                        return (T)stringResult;
                    }
                }
                return default(T);
            }
        }
        
        public void ReadDataFromDB(string sql, Func<DbDataReader, bool> processRow)
        {
            ReadDataFromDB(new SQLQueryWithParameters(sql, processRow));
        }
        
        public void ReadDataFromDB(SQLQueryWithParameters query)
        {
            query.RowCount = 0;
            using (var conn = CreateConnection())
            {
                conn.Open();
                var command = CreateCommand(query.SQL, conn);
                AddParameters(command, query);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        query.RowCount++;
                        if (!query.ProcessRow(reader))
                        {
                            break;
                        }
                    }
                    reader.Close();
                }
                conn.Close();
            }
        }

        private void ProcessIdentityColumn(NonQueryWithParameters query, DbConnection conn, DbTransaction transaction = null)
        {
            if (query.SetPrimaryKey != null)
            {
                query.SetPrimaryKey(GetIdentity(conn, transaction), query);
            }
        }

        private int GetIdentity(DbConnection connection, DbTransaction transaction = null)
        {
            DbCommand command;
            if (this.DataProvider.Contains("SqlServerCe"))
            {
                command = CreateCommand("SELECT @@IDENTITY;", connection, transaction);
                object newId = command.ExecuteScalar();
                if (newId != DBNull.Value)
                {
                    decimal id = (decimal)newId;
                    return (int)id;
                }
            }
            else if (this.DataProvider.Contains("SqlServer"))
            {
                command = CreateCommand("SELECT SCOPE_IDENTITY();", connection, transaction);
                object newId = command.ExecuteScalar();
                if (newId != DBNull.Value)
                {
                    decimal id = (decimal)newId;
                    return (int)id;
                }
            }
            else if (this.DataProvider.Contains("SQLite"))
            {
                command = CreateCommand("SELECT last_insert_rowid();", connection, transaction);
                object newId = command.ExecuteScalar();
                if (newId != DBNull.Value)
                {
                    long id = (long)newId;
                    return (int)id;
                }
            }
            else
            {
                throw new ApplicationException("Unknown provider type for retrieving identity column.");
            }
            return -1;
        }

        private void AddParameters(DbCommand command, SQLQuery query)
        {
            if (query.BuildParameters != null)
            {
                query.BuildParameters(query);
            }
            foreach (string paramName in query.Parameters.Keys)
            {
                command.Parameters.Add(CreateParameter(paramName, query.Parameters[paramName]));
            }
        }

        private DbParameter CreateParameter(string name, object value)
        {
            var parameter = DbFactory.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            return parameter;
        }

        private DbConnection CreateConnection()
        {
            var connection = DbFactory.CreateConnection();
            connection.ConnectionString = ConnectionString;
            return connection;
        }

        private DbCommand CreateCommand(string sql, DbConnection connection, DbTransaction transaction = null)
        {
            var command = DbFactory.CreateCommand();
            command.Connection = connection;
            command.CommandText = sql;
            if (transaction != null)
            {
                command.Transaction = transaction;
            }
            return command;
        }
    }
    public abstract class SQLQuery {
        public SQLQuery(string sql) {
            this.SQL = sql;
            Parameters = new Dictionary<string, object>();
        }
        public readonly Dictionary<string, object> Parameters;
        public Action<SQLQuery> BuildParameters { get; set; }
        public readonly string SQL;
        public int RowCount { get; set; }
    }

    public class SQLQueryWithParameters : SQLQuery
    {
        public SQLQueryWithParameters(string sql,Func<DbDataReader, bool> processRow)
            : base(sql)
        {
            if (processRow == null)
            {
                //throw new ArgumentException("You must specify an Func to process each row as it is read.", "processRow");
                this.ProcessRow = new Func<DbDataReader, bool>(dr => { return true; });
            }
            this.ProcessRow = processRow;
        }
        public readonly Func<DbDataReader, bool> ProcessRow;
    }
    public class NonQueryWithParameters : SQLQuery
    {
        public NonQueryWithParameters(string sql)
            : base(sql)
        {
        }
        public Action<int, NonQueryWithParameters> SetPrimaryKey { get; set; }
        public int Order { get; set; }
        public object Tag { get; set; }
    }
}