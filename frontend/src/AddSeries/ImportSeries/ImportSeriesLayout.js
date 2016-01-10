var Marionette = require('marionette');
var reqres = require('reqres');
var TableView = require('Table/TableView');
var tpl = require('./ImportSeriesLayout.hbs');

const headers = [
  {
    name: 'type',
    label: ''
  },
  {
    name: 'name',
    label: 'Name'
  }
];

const EmptyView = Marionette.Layout.extend({
  template: tpl,

  regions: {
    result: '.import-region'
  },

  events: {
    'click .x-start': 'onStart'
  },

  initialize(options = {}) {
    this.term = options.term;
  },

  onStart() {
    var promise = reqres.request(reqres.SelectPath);
    promise.done(this.onPathSelected);
  },

  onPathSelected(options) {

  }
});

module.exports = EmptyView;
