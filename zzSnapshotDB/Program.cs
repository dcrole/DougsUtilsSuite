﻿using System;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Common;
using LogFile = UtilityClasses.LogFile;

// This console app will take a snapshot of a live SQL database and store it in a form
// that can later be used to compare and generate synchronisation scripts.
namespace zzSnapshotDB
{
    class Program
    {
        public static AppSettings Settings;
        public static LogFile log;
        public static SqlConnection db; // Database connection to use when retrieving schema
        public static Server ConnSqlServer;
        public static bool ContinueProcessing; // If this is set to false at any point, abort the process.
        public static Guid SnapshotGuid;
        public static string OutputFolder; // All output files will be written here
        public static string SnapshotFolder; // Each time we take a database snapshot, we'll write it to a unique folder
        public static string ObjectList = "Type,Schema,Name,SubType,CreateDate,ModifyDate";

        static void Main(string[] args)
        {
            Settings = AppSettings.Load();
            log = new LogFile(Settings.LogFilePath);
            log.MaxFileSizeInKb = 1024;
            DebugMessage(log, "Starting schema dump");
            SnapshotGuid = Guid.NewGuid(); // Generate a Guid for this database snapshot

            // Get templates folder - by default, folder called Templates inside the executable folder.
            string ApplicationFolder = Assembly.GetExecutingAssembly().GetName().CodeBase;
            ApplicationFolder = ApplicationFolder.Substring(8).Replace("/", "\\");
            ApplicationFolder = Path.GetDirectoryName(ApplicationFolder);
            OutputFolder = Path.Combine(ApplicationFolder, "Output");
            string TempName = Settings.SQLServerNameOrIP + " " + Settings.SQLDatabaseName + " ";
            TempName += DateTime.Now.ToString("yyyy-MM-dd hh_mm_ss");
            SnapshotFolder = Path.Combine(OutputFolder, TempName);

            // Create Snapshot Output folder and all parent folders as needed
            if (!Directory.Exists(SnapshotFolder))
            {
                Directory.CreateDirectory(SnapshotFolder);
            }

            // Write DB settings into snapshot folder to facilitate connecting to DB to apply sync changes
            string DBSettings = "";
            DBSettings += "SQLServerNameOrIP=" + Settings.SQLServerNameOrIP + "\r\n";
            DBSettings += "SQLUserName=" + Settings.SQLUserName + "\r\n";
            DBSettings += "SQLPassword=" + Settings.SQLPassword + "\r\n";
            DBSettings += "SQLDatabaseName=" + Settings.SQLDatabaseName + "\r\n";
            string DBSettingsFilePath = "DBSettings.ini";
            DBSettingsFilePath = Path.Combine(SnapshotFolder, DBSettingsFilePath);
            File.WriteAllText(DBSettingsFilePath, DBSettings);

            // Append current snapshot to list of snapshots
            string SnapshotInfoFilePath = "SnapshotInfo.csv";
            SnapshotInfoFilePath = Path.Combine(SnapshotFolder, SnapshotInfoFilePath);
            string SnapshotListInfo = "";
            if (!File.Exists(SnapshotInfoFilePath))
            {
                SnapshotListInfo = "Guid,SnapshotDescription";
            }
            SnapshotListInfo += "\r\n";
            SnapshotListInfo += SnapshotGuid.ToString() + ",\"";
            SnapshotListInfo += Settings.SQLServerNameOrIP + " " + Settings.SQLDatabaseName + " ";
            SnapshotListInfo += DateTime.Now.ToString("yyyy-MM-dd hh_mm_ss");
            SnapshotListInfo += "\"";
            File.AppendAllText(SnapshotInfoFilePath, SnapshotListInfo);

            OpenDBConnection();

            //ListSchemas();

            //ListStoredProcs();

            WriteStoredProcInfo();

            WriteFunctions();

            WriteTables();

            //ListTables();

            //ListUserDefinedFunctions();

            string ObjectListFileName = "ObjectList.csv";
            string ObjectListFilePath = Path.Combine(SnapshotFolder, ObjectListFileName);
            File.WriteAllText(ObjectListFilePath, ObjectList);

            Console.WriteLine("\r\n\r\nFinished. Press a key to exit the progam.");
            Console.ReadKey();
        }

        private static void WriteTables()
        {
            string TableList = "Schema,TableName,CreateDate,ModifyDate";
            string Schema, TableName, CreateDate, ModifyDate;
            StringCollection SQLScript;
            string SQLFinalScript = "";
            ScriptingOptions so = new ScriptingOptions();
            Database CurrentDatabase = ConnSqlServer.Databases[Settings.SQLDatabaseName];

            so.ClusteredIndexes = true;
            so.WithDependencies = false;
            so.Default = true;
            so.DriAll = true;
            so.IncludeHeaders = false;
            so.Indexes = true;
            so.Permissions = false;
            so.IncludeDatabaseRoleMemberships = false;


            foreach (Table table in CurrentDatabase.Tables)
            {
                SQLFinalScript = "";
                Schema = table.Schema;
                TableName = table.Name;
                CreateDate = table.CreateDate.ToString();
                ModifyDate = table.DateLastModified.ToString();
                TableList += "\r\n" + Schema + "," + TableName + "," + CreateDate + "," + ModifyDate;
                ObjectList += "\r\n" + "Table," + Schema + "," + TableName + ",," + CreateDate + "," + ModifyDate;
                Console.WriteLine("Table: " + Schema + "." + TableName);

                SQLScript = table.Script(so);
                foreach (string s in SQLScript)
                {
                    SQLFinalScript += s + "\r\n\r\n";
                }
                string TableFileName = "TB_" + Schema + "_" + TableName + ".sql";
                string TableFilePath = Path.Combine(SnapshotFolder, TableFileName);
                File.WriteAllText(TableFilePath, SQLFinalScript);
            }

            string TableListFileName = "TableList.csv";
            string TableListFilePath = Path.Combine(SnapshotFolder, TableListFileName);
            File.WriteAllText(TableListFilePath, TableList);

            Console.WriteLine();
        }

        /// <summary>
        /// Write all user defined functions to files
        /// </summary>
        private static void WriteFunctions()
        {
            SqlCommand command = db.CreateCommand();
            string CmdText = " Select SCHEMA_NAME(o.schema_id) [Schema] ";
            CmdText += "     , o.name ";
            CmdText += "     , o.type_desc FunctionType ";
            CmdText += "     , o.create_date ";
            CmdText += "     , o.modify_date ";
            CmdText += "     , OBJECT_DEFINITION (OBJECT_ID(s.name + '.' + o.name)) AS [Object Definition]  ";
            CmdText += " FROM sys.objects o ";
            CmdText += " INNER JOIN sys.schemas s ON s.schema_id = o.schema_id ";
            CmdText += " WHERE o.type_desc LIKE '%FUNCTION%' ";
            CmdText += " ORDER BY o.[name] ";
            command.CommandText = CmdText;
            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            string FunctionList = "Schema,FunctionName,FunctionType,CreateDate,ModifyDate";

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (i == (reader.FieldCount - 1))
                    {
                        // Write stored proc definition out to separate file
                        Console.WriteLine("Function: " + reader.GetValue(1));
                        string FunctionFileName = "FN_" + reader.GetValue(0) + "_" + reader.GetValue(1) + ".sql";
                        string FunctionFilePath = Path.Combine(SnapshotFolder, FunctionFileName);
                        File.WriteAllText(FunctionFilePath, reader.GetValue(i).ToString());
                    }
                    //Console.Write("{0} ", reader.GetValue(i));
                }
                FunctionList += "\r\n" + reader.GetValue(0) + "," + reader.GetValue(1) + "," + reader.GetValue(2) + "," + reader.GetValue(3) + "," + reader.GetValue(4);
                ObjectList += "\r\n" + "Function," + reader.GetValue(0) + "," + reader.GetValue(1) + "," + reader.GetValue(2) + "," + reader.GetValue(3) + "," + reader.GetValue(4);
                Console.WriteLine();
            }

            string FunctionListFileName = "FunctionList.csv";
            string FunctionListFilePath = Path.Combine(SnapshotFolder, FunctionListFileName);
            File.WriteAllText(FunctionListFilePath, FunctionList);

            Console.WriteLine();

            reader.Close();
            reader.Dispose();

        }

        public static void OpenDBConnection()
        {
            if (db == null)
            {
                try
                {
                    // Specify the provider name, server and database.
                    string providerName = "System.Data.SqlClient";

                    // Initialize the connection string builder for the
                    // underlying provider.
                    SqlConnectionStringBuilder sqlBuilder =
                    new SqlConnectionStringBuilder();

                    // Set the properties for the data source.
                    sqlBuilder.DataSource = Settings.SQLServerNameOrIP;
                    sqlBuilder.InitialCatalog = Settings.SQLDatabaseName;
                    sqlBuilder.IntegratedSecurity = false;
                    sqlBuilder.UserID = Settings.SQLUserName;
                    sqlBuilder.Password = Settings.SQLPassword;

                    // Build the SqlConnection connection string.
                    string providerString = sqlBuilder.ToString();

                    db = new SqlConnection(sqlBuilder.ToString());

                    db.Open();
                }
                catch (Exception ex)
                {
                    ContinueProcessing = false;
                    string Message = "Error while trying to open a connection to the database.";
                    DebugMessage(log, Message, "Database", ex, true);
                }
            }

            if (ConnSqlServer == null)
            {
                try
                {
                    ServerConnection srvConn2 = new ServerConnection(Settings.SQLServerNameOrIP);
                    srvConn2.LoginSecure = false;
                    srvConn2.Login = Settings.SQLUserName;
                    srvConn2.Password = Settings.SQLPassword;
                    ConnSqlServer = new Server(srvConn2);
                }
                catch (Exception ex)
                {
                    ContinueProcessing = false;
                    string Message = "Error while trying to open a management connection to the database.";
                    DebugMessage(log, Message, "Database", ex, true);
                }
            }
        }

        // Write debug message to console and log file
        public static void DebugMessage(LogFile log, string Message, string Category = "", Exception ex = null, bool WriteStackTrace = false)
        {
            if (!string.IsNullOrEmpty(Category))
            {
                Message = Category + ": " + Message;
            }

            if (ex != null)
            {
                Message = Message + "\r\nEXCEPTION: " + ex.Message;

                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;

                    Message = Message + "\r\nINNER EXCEPTION: " + ex.Message;
                }

                if (WriteStackTrace)
                {
                    Message = Message + "\r\nSTACK TRACE: " + ex.StackTrace;
                }
            }

            Console.WriteLine(Message);
            log.Write(Message);
        }

        public static void ListSchemas()
        {
            SqlCommand command = db.CreateCommand();
            command.CommandText = "SELECT * FROM sys.schemas ORDER BY schema_id";
            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            Console.WriteLine("Schemas");
            Console.WriteLine("=======\r\n");

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                    Console.Write("{0} ", reader.GetValue(i));
                Console.WriteLine();
            }
            Console.WriteLine();

            reader.Close();
            reader.Dispose();
        }

        // List all Stored Procs, especially last modified date, to hint at the most recent version
        public static void ListStoredProcs()
        {
            SqlCommand command = db.CreateCommand();
            string CmdText = "SELECT TOP 5 s.name [Schema], p.name, o.modify_date, ";
            CmdText += "OBJECT_DEFINITION (OBJECT_ID(s.name + '.' + p.name)) AS [Object Definition] ";
            CmdText += "FROM sys.procedures p";
            CmdText += " INNER JOIN sys.objects o ON p.object_id = o.object_id";
            CmdText += " INNER JOIN sys.schemas s ON s.schema_id = p.schema_id";
            CmdText += " ORDER BY p.name";
            command.CommandText = CmdText;
            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            Console.WriteLine("Stored Procs");
            Console.WriteLine("============\r\n");

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (i == (reader.FieldCount - 1))
                    {
                        Console.WriteLine("\r\n\r\nSTORED PROC DEFINITION:");
                        Console.WriteLine("=======================\r\n\r\n");
                    }
                    Console.Write("{0} ", reader.GetValue(i));
                }
                Console.WriteLine();
            }

            Console.WriteLine();

            reader.Close();
            reader.Dispose();
        }

        public static void WriteStoredProcInfo()
        {
            SqlCommand command = db.CreateCommand();
            string CmdText = "SELECT s.name [Schema], p.name, o.create_date, o.modify_date, ";
            CmdText += "OBJECT_DEFINITION (OBJECT_ID(s.name + '.' + p.name)) AS [Object Definition] ";
            CmdText += "FROM sys.procedures p";
            CmdText += " INNER JOIN sys.objects o ON p.object_id = o.object_id";
            CmdText += " INNER JOIN sys.schemas s ON s.schema_id = p.schema_id";
            CmdText += " ORDER BY p.name";
            command.CommandText = CmdText;
            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            string StoredProcList = "Schema,ProcName,CreateDate,ModifyDate";

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (i == (reader.FieldCount - 1))
                    {
                        // Write stored proc definition out to separate file
                        Console.WriteLine("Stored Proc: " + reader.GetValue(1));
                        string StoredProcFileName = "SP_" + reader.GetValue(0) + "_" + reader.GetValue(1) + ".sql";
                        string StoredProcFilePath = Path.Combine(SnapshotFolder, StoredProcFileName);
                        File.WriteAllText(StoredProcFilePath, reader.GetValue(i).ToString());
                    }
                    //Console.Write("{0} ", reader.GetValue(i));
                }
                StoredProcList += "\r\n" + reader.GetValue(0) + "," + reader.GetValue(1) + "," + reader.GetValue(2) + "," + reader.GetValue(3);
                ObjectList += "\r\n" + "StoredProc," + reader.GetValue(0) + "," + reader.GetValue(1) + ",," + reader.GetValue(2) + "," + reader.GetValue(3);
                Console.WriteLine();
            }

            string StoredProcListFileName = "StoredProcList.csv";
            string StoredProcListFilePath = Path.Combine(SnapshotFolder, StoredProcListFileName);
            File.WriteAllText(StoredProcListFilePath, StoredProcList);

            Console.WriteLine();

            reader.Close();
            reader.Dispose();
        }


        // List all Tables
        public static void ListTables()
        {
            SqlCommand command = db.CreateCommand();
            command.CommandText = "SELECT t.name FROM sys.tables t INNER JOIN sys.objects o ON t.object_id = o.object_id ORDER BY t.name";
            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            Console.WriteLine("Tables");
            Console.WriteLine("======\r\n");

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                    Console.Write("{0} ", reader.GetValue(i));
                Console.WriteLine();
            }

            Console.WriteLine();

            reader.Close();
            reader.Dispose();
        }


        // List all Tables
        public static void ListUserDefinedFunctions()
        {
            SqlCommand command = db.CreateCommand();
            command.CommandText = "SELECT name AS function_name ,SCHEMA_NAME(schema_id) AS schema_name, type_desc FROM sys.objects WHERE type_desc LIKE '%FUNCTION%';";

            command.CommandTimeout = 30;
            command.CommandType = CommandType.Text;

            SqlDataReader reader = command.ExecuteReader();

            Console.WriteLine("User-defined functions");
            Console.WriteLine("======================\r\n");

            // Display column headers
            for (int i = 0; i < reader.FieldCount; i++)
                Console.Write("{0} ", reader.GetName(i));

            Console.WriteLine();

            // Display the data within the reader.
            while (reader.Read())
            {
                // Display all the columns. 
                for (int i = 0; i < reader.FieldCount; i++)
                    Console.Write("{0} ", reader.GetValue(i));
                Console.WriteLine();
            }

            Console.WriteLine();

            reader.Close();
            reader.Dispose();
        }

    }
}
