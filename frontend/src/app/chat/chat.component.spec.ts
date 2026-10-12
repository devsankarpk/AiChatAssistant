import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../environments/environment';
import { ChatComponent } from './chat.component';

describe('ChatComponent', () => {
  let component: ChatComponent;
  let fixture: ComponentFixture<ChatComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(ChatComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`).flush([]);
  });

  afterEach(() => httpMock.verify());

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('does not send an empty or whitespace-only draft', () => {
    component.draft.set('   ');
    void component.send();
    expect(component.sending()).toBeFalse();
    httpMock.expectNone(() => true);
  });

  it('fills the composer when an example prompt is used', () => {
    component.useStarter(component.starters[0]);
    expect(component.draft()).toBe(component.starters[0]);
  });

  it('copies a reply to the clipboard and shows "Copied" briefly', async () => {
    const writeText = jasmine.createSpy('writeText').and.resolveTo();
    spyOnProperty(navigator, 'clipboard', 'get').and.returnValue({ writeText } as unknown as Clipboard);
    jasmine.clock().install();

    try {
      await component.copy({ id: 7, role: 'assistant', content: '**hi**', createdAt: '' });
      expect(writeText).toHaveBeenCalledWith('**hi**');
      expect(component.copiedId()).toBe(7);

      jasmine.clock().tick(2000);
      expect(component.copiedId()).toBeNull();
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('does not claim "Copied" when the clipboard is blocked', async () => {
    const writeText = jasmine.createSpy('writeText').and.rejectWith(new Error('denied'));
    spyOnProperty(navigator, 'clipboard', 'get').and.returnValue({ writeText } as unknown as Clipboard);

    await component.copy({ id: 7, role: 'assistant', content: 'x', createdAt: '' });

    expect(component.copiedId()).toBeNull();
  });
});
