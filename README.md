The article tries to address the main issues that are involved when you try to migrate your disk-based database 
to In-Memory technology. 
Converting a SQLServer Database to InMemory OLTP 
https://www.red-gate.com/simple-talk/sql/t-sql-programming/converting-database-memory-oltp/

The largest database I ported to In-Memory OLTP by using this tool, was 15 GB in size and contains 1200 tables, 2200 indexes, and 2600 relations. The largest table was 4 GB in size.


## :white_check_mark: To convert your database to In-Memory OLTP execute following code

```diff
-            // private Inputs i = null; the class that holds all inputs ( e.g. server name, type of authentication etc. )
-            var cnn = new ServerConnection(i.serverName);
-            cnn.Connect();
-            var server = new Server(cnn);
-            // The disk based database
-            var db = server.Databases[i.databaseName];
-            // Connect to the In-Memory Database
-            var cnnInMem = new ServerConnection(i.serverName);
-            cnnInMem.Connect();
-            var serverInMem = new Server(cnnInMem);
-            var dbInMemory = serverInMem.Databases[i.inMemoryDataBaseName];
-
-            // new features available starting with SQL Server 2017
-            var enumFeatures = SQLServerMoFeatures.SQLServer2016;
-            if (new Version(server.VersionString) >= new Version(C_NEW_FEATURES_VERSION))
-                enumFeatures = SQLServerMoFeatures.SQLServer2017;
-            // Switch to In-Memory 
-            success = db.SwichToMo(
-                                    dbInMemory,     // In-Memory database
-                                    (ILog)this,     // logger
-                                    cnf,            // configuration class
-                                    o,              // options
-                                    enumFeatures);
-
-     
```

