mergeInto(LibraryManager.library, {
  ZMAssetIndexedDbGet: function (databaseNamePtr, keyPtr, requestId, success, error) {
    // IL2CPP 传入的 UTF-8 指针只保证在本次原生调用期间有效；IndexedDB 回调触发时
    // 原内存可能已经复用，因此所有输入必须在进入异步边界前复制为 JS 字符串。
    var databaseName = UTF8ToString(databaseNamePtr);
    var key = UTF8ToString(keyPtr);
    ZMAssetIndexedDb.open(databaseName, function (db) {
      try {
        var request = db.transaction('metadata', 'readonly').objectStore('metadata').get(key);
        request.onsuccess = function () {
          var value = request.result;
          var valuePtr = value === undefined ? 0 : stringToNewUTF8(value);
          {{{ makeDynCall('vii', 'success') }}}(requestId, valuePtr);
          if (valuePtr) _free(valuePtr);
        };
        request.onerror = function () { ZMAssetIndexedDb.fail(requestId, error, request.error); };
      } catch (exception) {
        ZMAssetIndexedDb.fail(requestId, error, exception);
      }
    }, requestId, error);
  },

  ZMAssetIndexedDbPut: function (databaseNamePtr, keyPtr, valuePtr, requestId, success, error) {
    var databaseName = UTF8ToString(databaseNamePtr);
    var key = UTF8ToString(keyPtr);
    var value = UTF8ToString(valuePtr);
    ZMAssetIndexedDb.open(databaseName, function (db) {
      var settled = false;
      var failOnce = function (reason) {
        if (settled) return;
        settled = true;
        ZMAssetIndexedDb.fail(requestId, error, reason);
      };
      try {
        var transaction = db.transaction('metadata', 'readwrite');
        transaction.objectStore('metadata').put(value, key);
        transaction.oncomplete = function () {
          if (settled) return;
          settled = true;
          {{{ makeDynCall('vii', 'success') }}}(requestId, 0);
        };
        transaction.onerror = function () { failOnce(transaction.error); };
        transaction.onabort = function () { failOnce(transaction.error || 'transaction aborted'); };
      } catch (exception) {
        failOnce(exception);
      }
    }, requestId, error);
  },

  ZMAssetIndexedDbDelete: function (databaseNamePtr, keyPtr, requestId, success, error) {
    var databaseName = UTF8ToString(databaseNamePtr);
    var key = UTF8ToString(keyPtr);
    ZMAssetIndexedDb.open(databaseName, function (db) {
      var settled = false;
      var failOnce = function (reason) {
        if (settled) return;
        settled = true;
        ZMAssetIndexedDb.fail(requestId, error, reason);
      };
      try {
        var transaction = db.transaction('metadata', 'readwrite');
        transaction.objectStore('metadata').delete(key);
        transaction.oncomplete = function () {
          if (settled) return;
          settled = true;
          {{{ makeDynCall('vii', 'success') }}}(requestId, 0);
        };
        transaction.onerror = function () { failOnce(transaction.error); };
        transaction.onabort = function () { failOnce(transaction.error || 'transaction aborted'); };
      } catch (exception) {
        failOnce(exception);
      }
    }, requestId, error);
  },

  ZMAssetIndexedDbList: function (databaseNamePtr, prefixPtr, requestId, success, error) {
    var databaseName = UTF8ToString(databaseNamePtr);
    var prefix = UTF8ToString(prefixPtr);
    ZMAssetIndexedDb.open(databaseName, function (db) {
      try {
        var keys = [];
        var request = db.transaction('metadata', 'readonly').objectStore('metadata').openKeyCursor();
        request.onsuccess = function () {
          var cursor = request.result;
          if (cursor) {
            if (typeof cursor.key === 'string' && cursor.key.indexOf(prefix) === 0) keys.push(cursor.key);
            cursor.continue();
            return;
          }
          var resultPtr = stringToNewUTF8(JSON.stringify(keys));
          {{{ makeDynCall('vii', 'success') }}}(requestId, resultPtr);
          _free(resultPtr);
        };
        request.onerror = function () { ZMAssetIndexedDb.fail(requestId, error, request.error); };
      } catch (exception) {
        ZMAssetIndexedDb.fail(requestId, error, exception);
      }
    }, requestId, error);
  },

  $ZMAssetIndexedDb: {
    databases: {},
    open: function (name, onSuccess, requestId, error) {
      if (ZMAssetIndexedDb.databases[name]) {
        onSuccess(ZMAssetIndexedDb.databases[name]);
        return;
      }
      var request = indexedDB.open(name, 1);
      request.onupgradeneeded = function () {
        if (!request.result.objectStoreNames.contains('metadata')) request.result.createObjectStore('metadata');
      };
      request.onsuccess = function () {
        ZMAssetIndexedDb.databases[name] = request.result;
        onSuccess(request.result);
      };
      request.onerror = function () { ZMAssetIndexedDb.fail(requestId, error, request.error); };
      request.onblocked = function () { ZMAssetIndexedDb.fail(requestId, error, 'database open blocked'); };
    },
    fail: function (requestId, error, value) {
      var message = value && value.message ? value.message : String(value || 'unknown IndexedDB error');
      var messagePtr = stringToNewUTF8(message);
      {{{ makeDynCall('vii', 'error') }}}(requestId, messagePtr);
      _free(messagePtr);
    }
  }
});

autoAddDeps(LibraryManager.library, '$ZMAssetIndexedDb');
