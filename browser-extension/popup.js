'use strict';

const checkbox = document.getElementById('autoFollow');
const difficulty = document.getElementById('difficulty');
const restoreIgnored = document.getElementById('restoreIgnored');

chrome.storage.local.get({ autoFollow: false, difficulty: 'standard' }, (result) => {
  checkbox.checked = !!result.autoFollow;
  difficulty.value = ['concise', 'standard', 'detailed'].includes(result.difficulty)
    ? result.difficulty : 'standard';
});

checkbox.addEventListener('change', () => {
  chrome.storage.local.set({ autoFollow: checkbox.checked });
});

difficulty.addEventListener('change', () => {
  chrome.storage.local.set({ difficulty: difficulty.value });
});

restoreIgnored.addEventListener('click', () => {
  chrome.storage.local.set({ ignoredTerms: [] }, () => {
    restoreIgnored.textContent = '已恢复';
    setTimeout(() => { restoreIgnored.textContent = '恢复所有被忽略的词'; }, 1200);
  });
});
