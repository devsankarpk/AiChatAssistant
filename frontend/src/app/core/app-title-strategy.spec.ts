import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, TitleStrategy, provideRouter } from '@angular/router';

import { AppTitleStrategy } from './app-title-strategy';

@Component({ template: '' })
class Blank {}

describe('AppTitleStrategy', () => {
  let router: Router;
  let title: Title;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', title: 'Log in', component: Blank },
          { path: 'chat', title: 'Chat', component: Blank },
          { path: 'untitled', component: Blank },
        ]),
        { provide: TitleStrategy, useClass: AppTitleStrategy },
      ],
    });
    router = TestBed.inject(Router);
    title = TestBed.inject(Title);
  });

  it('sets "<page> - AI Chat Assistant" from the route title', async () => {
    await router.navigateByUrl('/login');

    expect(title.getTitle()).toBe('Log in - AI Chat Assistant');
  });

  it('changes the title when the route changes', async () => {
    await router.navigateByUrl('/login');
    await router.navigateByUrl('/chat');

    expect(title.getTitle()).toBe('Chat - AI Chat Assistant');
  });

  it('uses just the app name for a route without a title', async () => {
    await router.navigateByUrl('/login');
    await router.navigateByUrl('/untitled');

    expect(title.getTitle()).toBe('AI Chat Assistant');
  });
});
