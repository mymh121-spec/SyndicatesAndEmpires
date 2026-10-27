mergeInto(LibraryManager.library, {
  $SE_SaveRequest: function(receiver, id, key, value, write) {
    var finished = false;
    var activeTransaction = null;
    var timeout = setTimeout(function() {
      if (activeTransaction) { try { activeTransaction.abort(); } catch (error) {} }
      respond(null, '브라우저 저장 응답 시간이 초과되었습니다.');
    }, 12000);
    function respond(result, error) {
      if (finished) return;
      finished = true;
      clearTimeout(timeout);
      SendMessage(receiver, 'OnStorageResponse', JSON.stringify({
        id: id, value: result === undefined ? null : result, error: error || ''
      }));
    }
    try {
      var request = indexedDB.open('SyndicatesAndEmpires.SinglePlayer', 1);
      request.onupgradeneeded = function() {
        if (!request.result.objectStoreNames.contains('saves'))
          request.result.createObjectStore('saves');
      };
      request.onerror = function() { respond(null, '브라우저 저장 공간을 열 수 없습니다.'); };
      request.onblocked = function() { respond(null, '다른 게임 탭을 닫고 다시 시도하세요.'); };
      request.onsuccess = function() {
        var db = request.result;
        if (finished) { db.close(); return; }
        try {
          var tx = db.transaction('saves', write ? 'readwrite' : 'readonly');
          activeTransaction = tx;
          var store = tx.objectStore('saves');
          var result = null;
          var operation = write ? store.put(value, key) : store.get(key);
          operation.onsuccess = function() { if (!write) result = operation.result; };
          tx.oncomplete = function() { db.close(); respond(result, ''); };
          tx.onabort = tx.onerror = function() {
            db.close(); respond(null, '브라우저 저장에 실패했습니다. 저장 공간과 개인정보 보호 설정을 확인하세요.');
          };
        } catch (error) { db.close(); respond(null, '브라우저 저장을 사용할 수 없습니다.'); }
      };
    } catch (error) { respond(null, '이 브라우저에서 저장 공간을 사용할 수 없습니다.'); }
  },
  SE_SaveRead__deps: ['$SE_SaveRequest', '$UTF8ToString'],
  SE_SaveRead: function(receiver, id, key) {
    SE_SaveRequest(UTF8ToString(receiver), id, UTF8ToString(key), null, false);
  },
  SE_SaveWrite__deps: ['$SE_SaveRequest', '$UTF8ToString'],
  SE_SaveWrite: function(receiver, id, key, value) {
    SE_SaveRequest(UTF8ToString(receiver), id, UTF8ToString(key), UTF8ToString(value), true);
  }
});
